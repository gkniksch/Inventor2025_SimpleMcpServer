using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using Inventor;

namespace InventorMcpServer.Services;

/// <summary>
/// Information stored for an entity registered with an opaque handle.
/// </summary>
public sealed class EntityEntry
{
    public required string Handle { get; init; }
    public required object ComObject { get; init; }
    public byte[]? ReferenceKey { get; init; }
    public string? KeyString { get; init; }
    public string? Description { get; init; }
    public string EntityType { get; init; } = string.Empty;
    public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Thread-safe registry mapping short opaque handles (e.g. 'face_1', 'occ_2', 'ent_3') to live COM objects
/// and ReferenceKey byte arrays so AI agents can reliably pass geometric references across tool invocations.
/// </summary>
public sealed class EntityRegistry
{
    private readonly ConcurrentDictionary<string, EntityEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<object, string> _objectToHandle = new();
    private int _counter;
    private string? _lastDocumentIdentifier;

    /// <summary>
    /// Registers a COM entity and returns a short handle.
    /// If the entity was already registered in the current session, returns the existing handle.
    /// </summary>
    public string Register(object entity, string prefix = "ent", string description = "", _Document? contextDoc = null)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // Normalize prefix
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "ent";
        prefix = prefix.Trim().ToLowerInvariant();

        // Check if object is already registered
        if (_objectToHandle.TryGetValue(entity, out var existingHandle))
        {
            return existingHandle;
        }

        int id = Interlocked.Increment(ref _counter);
        string handle = $"{prefix}_{id}";

        byte[]? refKey = TryExtractReferenceKey(entity, contextDoc, out string? keyStr);

        var entry = new EntityEntry
        {
            Handle = handle,
            ComObject = entity,
            ReferenceKey = refKey,
            KeyString = keyStr,
            Description = description,
            EntityType = entity.GetType().Name
        };

        _entries[handle] = entry;
        _objectToHandle[entity] = handle;
        return handle;
    }

    /// <summary>
    /// Resolves an opaque handle to its live COM object. Throws KeyNotFoundException if not found.
    /// </summary>
    public object Get(string handle)
    {
        if (TryGet(handle, out var entity) && entity != null)
        {
            return entity;
        }

        throw new KeyNotFoundException(
            $"Entity handle '{handle}' was not found in the registry. " +
            "It may have expired, or the registry was cleared when switching documents. " +
            "Query geometry again to obtain fresh handles.");
    }

    /// <summary>
    /// Resolves an opaque handle to its typed COM object.
    /// </summary>
    public T Get<T>(string handle) where T : class
    {
        var obj = Get(handle);
        if (obj is T typed)
        {
            return typed;
        }

        throw new InvalidCastException(
            $"Entity handle '{handle}' references an object of type '{obj.GetType().FullName}', which cannot be cast to '{typeof(T).FullName}'.");
    }

    /// <summary>
    /// Attempts to resolve an opaque handle.
    /// </summary>
    public bool TryGet(string handle, out object? entity)
    {
        entity = null;
        if (string.IsNullOrWhiteSpace(handle)) return false;

        if (_entries.TryGetValue(handle.Trim(), out var entry))
        {
            entity = entry.ComObject;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets metadata for a registered entity handle.
    /// </summary>
    public EntityEntry? GetEntry(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        _entries.TryGetValue(handle.Trim(), out var entry);
        return entry;
    }

    /// <summary>
    /// Clears all stored entity handles.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        _objectToHandle.Clear();
        Interlocked.Exchange(ref _counter, 0);
    }

    /// <summary>
    /// Clears the registry if the active document has changed.
    /// </summary>
    public void CheckActiveDocument(_Document? doc)
    {
        string currentId = doc == null ? string.Empty : $"{doc.FullFileName}|{doc.DisplayName}";
        if (_lastDocumentIdentifier != null && !string.Equals(_lastDocumentIdentifier, currentId, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"[EntityRegistry] Active document changed from '{_lastDocumentIdentifier}' to '{currentId}'. Clearing registered handles.");
            Clear();
        }
        _lastDocumentIdentifier = currentId;
    }

    /// <summary>
    /// Attempts to extract an Inventor ReferenceKey byte array from the COM object.
    /// </summary>
    private static byte[]? TryExtractReferenceKey(object entity, _Document? doc, out string? keyStr)
    {
        keyStr = null;
        try
        {
            // Check for GetReferenceKey method on the entity
            var method = entity.GetType().GetMethod("GetReferenceKey");
            if (method != null)
            {
                byte[] refKey = [];
                int keyContext = 0;
                if (doc != null)
                {
                    keyContext = doc.ReferenceKeyManager.CreateKeyContext();
                }

                object?[] args = [refKey, keyContext];
                method.Invoke(entity, args);

                if (args[0] is byte[] resultKey && resultKey.Length > 0)
                {
                    if (doc != null)
                    {
                        try
                        {
                            keyStr = doc.ReferenceKeyManager.KeyToString(ref resultKey);
                        }
                        catch
                        {
                            keyStr = Convert.ToBase64String(resultKey);
                        }
                    }
                    return resultKey;
                }
            }
        }
        catch
        {
            // Silently ignore if the entity does not support persistent reference keys
        }

        return null;
    }
}
