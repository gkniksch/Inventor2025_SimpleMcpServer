using System;
using System.Runtime.InteropServices;
using Inventor;

namespace InventorMcpServer.Services;

/// <summary>
/// Manages the connection to an active Autodesk Inventor 2025 instance via the Windows Running Object Table (ROT).
/// Does not launch a headless background Inventor process. Automatically detects COM disconnections and reconnects.
/// </summary>
public sealed class InventorSessionManager
{
    private const string InventorProgId = "Inventor.Application";

    // Common COM disconnection HRESULTs
    private const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
    private const int RPC_E_SERVERFAULT = unchecked((int)0x80010105);
    private const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);
    private const int RPC_E_SERVER_DIED = unchecked((int)0x800706BE);
    private const int CO_E_OBJNOTCONNECTED = unchecked((int)0x800401FD);
    private const int MK_E_UNAVAILABLE = unchecked((int)0x800401E3);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid lpclsid);

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int GetActiveObject([In] ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private readonly object _syncLock = new();
    private Inventor.Application? _cachedApp;

    /// <summary>
    /// Gets the current active Inventor.Application instance, or attaches to one in the ROT.
    /// If Inventor is not running, throws an actionable InvalidOperationException.
    /// </summary>
    public Inventor.Application GetApplication()
    {
        lock (_syncLock)
        {
            if (_cachedApp != null)
            {
                if (IsAlive(_cachedApp))
                {
                    return _cachedApp;
                }

                Console.Error.WriteLine("[InventorSessionManager] Cached Inventor.Application instance is disconnected. Reconnecting via ROT...");
                _cachedApp = null;
            }

            _cachedApp = AttachToActiveInventor();
            return _cachedApp;
        }
    }

    /// <summary>
    /// Checks if Autodesk Inventor is currently running and registered in the ROT.
    /// </summary>
    public bool IsInventorRunning()
    {
        lock (_syncLock)
        {
            if (_cachedApp != null && IsAlive(_cachedApp))
            {
                return true;
            }

            try
            {
                var app = AttachToActiveInventor();
                _cachedApp = app;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Attaches to the active Inventor instance registered in the Windows Running Object Table.
    /// Throws InvalidOperationException if Inventor is not running.
    /// </summary>
    private static Inventor.Application AttachToActiveInventor()
    {
        int hrClsid = CLSIDFromProgID(InventorProgId, out Guid clsid);
        if (hrClsid != 0)
        {
            throw new InvalidOperationException(
                $"Failed to resolve CLSID for ProgID '{InventorProgId}' (HRESULT 0x{hrClsid:X8}). " +
                "Ensure Autodesk Inventor 2025 is installed correctly on this machine.");
        }

        int hrActive = GetActiveObject(ref clsid, IntPtr.Zero, out object? punk);
        if (hrActive != 0 || punk == null)
        {
            if (hrActive == MK_E_UNAVAILABLE)
            {
                throw new InvalidOperationException(
                    "Autodesk Inventor 2025 is not running or not registered in the Windows Running Object Table (ROT). " +
                    "Please launch Autodesk Inventor 2025 and ensure it is ready before invoking tools.");
            }

            var innerEx = Marshal.GetExceptionForHR(hrActive);
            throw new InvalidOperationException(
                $"Failed to connect to active Autodesk Inventor 2025 instance (HRESULT 0x{hrActive:X8}: {innerEx?.Message}). " +
                "Please verify that Autodesk Inventor 2025 is running.", innerEx);
        }

        if (punk is Inventor.Application app)
        {
            Console.Error.WriteLine($"[InventorSessionManager] Successfully attached to Autodesk Inventor {app.SoftwareVersion.DisplayVersion}.");
            return app;
        }

        throw new InvalidOperationException(
            $"Object retrieved from ROT for '{InventorProgId}' does not implement Inventor.Application (actual type: {punk.GetType().FullName}).");
    }

    /// <summary>
    /// Tests whether the COM pointer is still valid and responsive.
    /// </summary>
    private static bool IsAlive(Inventor.Application app)
    {
        try
        {
            // Lightweight property query to check COM health
            _ = app.Ready;
            return true;
        }
        catch (COMException ex) when (IsDisconnectionHResult(ex.ErrorCode))
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsDisconnectionHResult(int hr)
    {
        return hr == RPC_E_DISCONNECTED
            || hr == RPC_E_SERVERFAULT
            || hr == RPC_S_SERVER_UNAVAILABLE
            || hr == RPC_E_SERVER_DIED
            || hr == CO_E_OBJNOTCONNECTED;
    }
}
