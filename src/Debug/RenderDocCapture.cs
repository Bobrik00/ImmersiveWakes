#if DEBUG
using System;
using System.Runtime.InteropServices;

namespace ImmersiveWakes.Debug;

// RenderDoc's in-application API (renderdoc_app.h, version 1.6.0), which is in the process only when renderdoccmd
// started the client (tools\IWk-autorun.ps1 -RenderDoc). TriggerCapture takes the next frame the window presents.
static class RenderDocCapture
{
    const int Version160 = 10600;
    // Positions in RENDERDOC_API_1_6_0, a table of function pointers (unions included, each one pointer wide).
    const int GetNumCapturesSlot = 13;
    const int TriggerCaptureSlot = 15;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetApiFn(int version, out IntPtr api);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void TriggerCaptureFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint GetNumCapturesFn();

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32", CharSet = CharSet.Ansi, BestFitMapping = false)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    public static string Trigger()
    {
        IntPtr api = Api();
        if (api == IntPtr.Zero) return "not attached, start the client with tools\\IWk-autorun.ps1 -RenderDoc";
        Slot<TriggerCaptureFn>(api, TriggerCaptureSlot)();
        return $"capture of the next frame triggered, {Slot<GetNumCapturesFn>(api, GetNumCapturesSlot)()} written before it";
    }

    static IntPtr Api()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        IntPtr module = GetModuleHandleW("renderdoc.dll");
        if (module == IntPtr.Zero) return IntPtr.Zero;
        IntPtr getApi = GetProcAddress(module, "RENDERDOC_GetAPI");
        if (getApi == IntPtr.Zero) return IntPtr.Zero;
        return Marshal.GetDelegateForFunctionPointer<GetApiFn>(getApi)(Version160, out IntPtr api) == 1 ? api : IntPtr.Zero;
    }

    static T Slot<T>(IntPtr api, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(api, index * IntPtr.Size));
}
#endif
