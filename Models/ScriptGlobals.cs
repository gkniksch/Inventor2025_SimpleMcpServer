using System;
using Inventor;

namespace InventorMcpServer.Models;

/// <summary>
/// Strongly-typed host globals provided to C# scripts evaluated by execute_inventor_csharp.
/// </summary>
public sealed class ScriptGlobals
{
    /// <summary>
    /// Active Autodesk Inventor Application instance.
    /// </summary>
    public required Inventor.Application App { get; init; }

    /// <summary>
    /// Active Inventor Document (PartDocument, AssemblyDocument, etc.). May be null if no document is open.
    /// </summary>
    public Inventor.Document? Doc { get; init; }

    /// <summary>
    /// TransientGeometry helper instance for creating points, vectors, matrices, lines, planes, etc.
    /// </summary>
    public required Inventor.TransientGeometry TG { get; init; }

    /// <summary>
    /// Resolves an opaque handle (e.g. 'face_1', 'occ_2') previously returned by query_geometry or get_document_context to its live COM object.
    /// </summary>
    public required Func<string, object> GetEntity { get; init; }

    /// <summary>
    /// Registers any COM entity into the EntityRegistry and returns a new opaque handle string.
    /// </summary>
    public required Func<object, string> RegisterEntity { get; init; }

    /// <summary>
    /// Appends a diagnostic log message returned in the tool response.
    /// </summary>
    public required Action<string> Log { get; init; }
}
