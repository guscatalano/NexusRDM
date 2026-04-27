using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NexusRDM.RdpAx;

/// <summary>
/// Bridges the mstscax OCX's <c>IMsTscAxEvents</c> dispinterface into the
/// session's <see cref="MstscAxRdpSession"/> log. Implemented as a minimal
/// IDispatch sink — we don't ship a tlbimp'd interop assembly, so we route
/// every dispid to a single Invoke handler that resolves the event name
/// from the COM type info and forwards it to the supplied logger.
///
/// Lifetime: Attach() returns an instance holding the connection-point
/// cookie; Dispose() unadvises so the OCX releases its sink reference.
/// </summary>
internal sealed class OcxEventSink : IDispatch, ICustomQueryInterface, IDisposable
{
    // IID_IMsTscAxEvents  — the dispinterface raised by mstscax.
    private static readonly Guid IID_IMsTscAxEvents =
        new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");

    private readonly Action<string, string> _log;
    private readonly Dictionary<int, string> _dispIdNames;
    private readonly IConnectionPoint _cp;
    private          int _cookie;

    private OcxEventSink(
        Action<string, string> log,
        Dictionary<int, string> names,
        IConnectionPoint cp)
    {
        _log         = log;
        _dispIdNames = names;
        _cp          = cp;
    }

    public static OcxEventSink Attach(object ocx, Action<string, string> log)
    {
        var cpc = (IConnectionPointContainer)ocx;
        var iid = IID_IMsTscAxEvents;
        cpc.FindConnectionPoint(ref iid, out var cp);
        if (cp is null) throw new InvalidOperationException("OCX missing IMsTscAxEvents connection point.");

        // Resolve dispid → event name by introspecting the source
        // dispinterface's type info. Falls back to "DispId(N)" for
        // anything we can't name.
        var names  = ResolveDispIdNames(ocx);
        var sink   = new OcxEventSink(log, names, cp);
        cp.Advise(sink, out var cookie);
        sink._cookie = cookie;
        return sink;
    }

    private static Dictionary<int, string> ResolveDispIdNames(object ocx)
    {
        var map = new Dictionary<int, string>();
        try
        {
            var provideClassInfo = (IProvideClassInfo)ocx;
            provideClassInfo.GetClassInfo(out var typeInfo);
            typeInfo.GetTypeAttr(out var pTypeAttr);
            try
            {
                var typeAttr = Marshal.PtrToStructure<TYPEATTR>(pTypeAttr);
                for (int i = 0; i < typeAttr.cImplTypes; i++)
                {
                    typeInfo.GetRefTypeOfImplType(i, out var href);
                    typeInfo.GetRefTypeInfo(href, out var refInfo);
                    refInfo.GetTypeAttr(out var pRefAttr);
                    try
                    {
                        var refAttr = Marshal.PtrToStructure<TYPEATTR>(pRefAttr);
                        if (refAttr.guid != IID_IMsTscAxEvents) continue;
                        for (int f = 0; f < refAttr.cFuncs; f++)
                        {
                            refInfo.GetFuncDesc(f, out var pFunc);
                            try
                            {
                                var func = Marshal.PtrToStructure<FUNCDESC>(pFunc);
                                var nameBuf = new string[1];
                                refInfo.GetNames(func.memid, nameBuf, 1, out _);
                                if (!string.IsNullOrEmpty(nameBuf[0]))
                                    map[func.memid] = nameBuf[0];
                            }
                            finally { refInfo.ReleaseFuncDesc(pFunc); }
                        }
                    }
                    finally { refInfo.ReleaseTypeAttr(pRefAttr); }
                }
            }
            finally { typeInfo.ReleaseTypeAttr(pTypeAttr); }
        }
        catch { /* best effort — falls through to DispId(N) */ }
        return map;
    }

    public void Dispose()
    {
        try { _cp.Unadvise(_cookie); } catch { /* OCX gone */ }
        try { Marshal.ReleaseComObject(_cp); } catch { }
    }

    /// <summary>
    /// IConnectionPoint::Advise QIs the sink for the source-dispinterface
    /// IID (here IMsTscAxEvents). Since we don't ship typed bindings for
    /// it, the runtime QI fails with E_NOINTERFACE → CONNECT_E_CANNOTCONNECT.
    /// Hand back our IDispatch pointer for that IID — connection points
    /// only call sinks via IDispatch::Invoke anyway.
    /// </summary>
    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
    {
        if (iid == IID_IMsTscAxEvents)
        {
            ppv = Marshal.GetComInterfaceForObject(this, typeof(IDispatch));
            return CustomQueryInterfaceResult.Handled;
        }
        ppv = IntPtr.Zero;
        return CustomQueryInterfaceResult.NotHandled;
    }

    // ── IDispatch ─────────────────────────────────────────────────────────

    int IDispatch.GetTypeInfoCount(out uint pctinfo)        { pctinfo = 0; return 0; }
    int IDispatch.GetTypeInfo(uint i, uint lcid, IntPtr pp) { return unchecked((int)0x80004001); /* E_NOTIMPL */ }
    int IDispatch.GetIDsOfNames(ref Guid riid, IntPtr names, uint c, uint lcid, IntPtr ids)
        => unchecked((int)0x80020006); /* DISP_E_UNKNOWNNAME */

    int IDispatch.Invoke(int dispId, ref Guid riid, uint lcid, ushort flags,
                         IntPtr pDispParams, IntPtr pVarResult,
                         IntPtr pExcepInfo, IntPtr pArgErr)
    {
        try
        {
            var name   = _dispIdNames.TryGetValue(dispId, out var n) ? n : $"DispId({dispId})";
            var detail = TryReadFirstArg(pDispParams);
            _log(name, detail);
        }
        catch { /* never let an event bubble back into the OCX */ }
        return 0;
    }

    private static string TryReadFirstArg(IntPtr pDispParams)
    {
        if (pDispParams == IntPtr.Zero) return string.Empty;
        try
        {
            var dp = Marshal.PtrToStructure<DISPPARAMS>(pDispParams);
            if (dp.cArgs == 0 || dp.rgvarg == IntPtr.Zero) return string.Empty;
            // VARIANT is 16 bytes on x64 (24 with padding actually). Use
            // GetObjectForNativeVariant to extract the topmost value —
            // the dispinterface conventions hand the "interesting" arg
            // last in the array, but rendering even one is informative.
            var v = Marshal.GetObjectForNativeVariant(dp.rgvarg);
            return v?.ToString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPPARAMS
    {
        public IntPtr rgvarg;
        public IntPtr rgdispidNamedArgs;
        public uint   cArgs;
        public uint   cNamedArgs;
    }

}

[ComImport]
[Guid("00020400-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDispatch
{
    [PreserveSig] int GetTypeInfoCount(out uint pctinfo);
    [PreserveSig] int GetTypeInfo(uint iTInfo, uint lcid, IntPtr ppTInfo);
    [PreserveSig] int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgDispId);
    [PreserveSig] int Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags,
                             IntPtr pDispParams, IntPtr pVarResult,
                             IntPtr pExcepInfo, IntPtr puArgErr);
}

[ComImport]
[Guid("B196B283-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProvideClassInfo
{
    void GetClassInfo([MarshalAs(UnmanagedType.Interface)] out ITypeInfo ti);
}
