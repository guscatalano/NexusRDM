using System.Runtime.InteropServices;

namespace NexusRDM.RdpAx;

/// <summary>
/// Side-loads a user-supplied <c>mstscax.dll</c> as the COM server
/// for <c>MsRdpClient9NotSafeForScripting</c>, replacing the
/// system-registered version for this process.
///
/// Mechanism: write a SxS manifest into a private temp folder, copy
/// the override DLL alongside it as <c>mstscax.dll</c>, then call
/// <c>CreateActCtx</c> + <c>ActivateActCtx</c>. While the activation
/// context is active on the calling thread, <c>CoCreateInstance</c>
/// resolves the registered CLSID against the manifest's
/// <c>&lt;comClass&gt;</c> entry — i.e. our copy wins.
/// </summary>
public static class MstscAxOverride
{
    private const string MsRdpClient9NotSafeClsid = "{A41A4187-5A86-4E26-B40A-856F9035D9CB}";

    private static IntPtr _ctx    = INVALID_HANDLE_VALUE;
    private static string? _path;
    private static readonly object _gate = new();

    /// <summary>Set the override path (or empty/null to clear).
    /// Idempotent — re-applying the same path is a no-op.</summary>
    public static void Configure(string? path)
    {
        lock (_gate)
        {
            if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return;
            _path = path;
            ReleaseContextLocked();

            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;

            try
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-mstscax");
                System.IO.Directory.CreateDirectory(dir);
                var dllDest = System.IO.Path.Combine(dir, "mstscax.dll");
                if (!FilesAreSame(path, dllDest))
                    System.IO.File.Copy(path, dllDest, overwrite: true);

                var manifestPath = System.IO.Path.Combine(dir, "override.manifest");
                System.IO.File.WriteAllText(manifestPath, BuildManifest());

                var actCtx = new ACTCTX
                {
                    cbSize    = (uint)Marshal.SizeOf<ACTCTX>(),
                    dwFlags   = 0,
                    lpSource  = manifestPath,
                };
                _ctx = CreateActCtx(ref actCtx);
            }
            catch
            {
                // Non-fatal: fall through with no context active so the
                // system mstscax keeps working.
                _ctx = INVALID_HANDLE_VALUE;
            }
        }
    }

    /// <summary>Activate the override on the calling thread for the
    /// duration of the returned <see cref="IDisposable"/>. Activation
    /// contexts are per-thread, so callers (the WinForms STA thread
    /// that hosts AxHost) must do this around the OCX construction.</summary>
    public static IDisposable Push()
    {
        if (_ctx == INVALID_HANDLE_VALUE) return NoopScope.Instance;
        if (!ActivateActCtx(_ctx, out var cookie)) return NoopScope.Instance;
        return new ContextScope(cookie);
    }

    /// <summary>Validation path: full <c>LoadLibrary</c>, look up
    /// <c>DllGetClassObject</c>, instantiate the class factory, and
    /// ask it for an instance. Anything short of "all of those
    /// succeeded" reports the failing step.</summary>
    public static (bool Ok, string Message) ValidateLoadsCom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (true, "Using registered COM class (system mstscax).");
        if (!System.IO.File.Exists(path)) return (false, "File not found.");

        var hModule = LoadLibrary(path);
        if (hModule == IntPtr.Zero)
            return (false, $"LoadLibrary failed (Win32 error 0x{Marshal.GetLastWin32Error():X}).");

        try
        {
            var proc = GetProcAddress(hModule, "DllGetClassObject");
            if (proc == IntPtr.Zero)
                return (false, "DLL is missing DllGetClassObject — not a COM server.");

            var dllGetClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectDelegate>(proc);
            var clsid = new Guid(MsRdpClient9NotSafeClsid);
            var iidClassFactory = new Guid("00000001-0000-0000-C000-000000000046");
            var hr = dllGetClassObject(ref clsid, ref iidClassFactory, out var pCf);
            if (hr != 0 || pCf == IntPtr.Zero)
                return (false, $"DllGetClassObject returned 0x{hr:X8}. The DLL doesn't supply MsRdpClient9NotSafeForScripting.");

            try
            {
                var cf = (IClassFactory)Marshal.GetObjectForIUnknown(pCf);
                var iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
                var hr2 = cf.CreateInstance(IntPtr.Zero, ref iidUnknown, out var pUnk);
                if (hr2 != 0 || pUnk == IntPtr.Zero)
                    return (false, $"IClassFactory::CreateInstance returned 0x{hr2:X8}.");
                Marshal.Release(pUnk);
                Marshal.ReleaseComObject(cf);
                return (true, "DLL loaded and produced a valid MsRdpClient9 COM instance.");
            }
            finally { Marshal.Release(pCf); }
        }
        finally { FreeLibrary(hModule); }
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private static string BuildManifest() => $@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
  <assemblyIdentity type=""win32"" name=""NexusRDM.MstscaxOverride"" version=""1.0.0.0""/>
  <file name=""mstscax.dll"">
    <comClass clsid=""{MsRdpClient9NotSafeClsid}"" threadingModel=""Apartment""/>
  </file>
</assembly>";

    private static bool FilesAreSame(string a, string b)
    {
        try
        {
            if (!System.IO.File.Exists(b)) return false;
            var fa = new System.IO.FileInfo(a);
            var fb = new System.IO.FileInfo(b);
            return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
        }
        catch { return false; }
    }

    private static void ReleaseContextLocked()
    {
        if (_ctx != INVALID_HANDLE_VALUE)
        {
            ReleaseActCtx(_ctx);
            _ctx = INVALID_HANDLE_VALUE;
        }
    }

    private sealed class ContextScope : IDisposable
    {
        private IntPtr _cookie;
        public ContextScope(IntPtr cookie) => _cookie = cookie;
        public void Dispose()
        {
            if (_cookie == IntPtr.Zero) return;
            try { DeactivateActCtx(0, _cookie); } catch { /* tearing down */ }
            _cookie = IntPtr.Zero;
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    // ── Win32 ─────────────────────────────────────────────────────────────

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ACTCTX
    {
        public uint    cbSize;
        public uint    dwFlags;
        public string  lpSource;
        public ushort  wProcessorArchitecture;
        public ushort  wLangId;
        public string? lpAssemblyDirectory;
        public string? lpResourceName;
        public string? lpApplicationName;
        public IntPtr  hModule;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateActCtx(ref ACTCTX pActCtx);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ActivateActCtx(IntPtr hActCtx, out IntPtr lpCookie);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeactivateActCtx(uint dwFlags, IntPtr lpCookie);

    [DllImport("kernel32.dll")]
    private static extern void ReleaseActCtx(IntPtr hActCtx);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    private delegate int DllGetClassObjectDelegate(ref Guid clsid, ref Guid iid, out IntPtr ppv);

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int LockServer(bool fLock);
    }
}
