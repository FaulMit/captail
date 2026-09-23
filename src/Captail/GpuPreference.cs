using System.Runtime.InteropServices;

namespace Captail;

internal static class GpuPreference
{
    [DllImport("CaptailObsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int captail_get_high_performance_adapter_index();

    internal static uint HighPerformanceAdapterIndex()
    {
        try
        {
            int index = captail_get_high_performance_adapter_index();
            if (index >= 0)
            {
                Log.Write($"GPU preference: high-performance adapter index={index}.");
                return (uint)index;
            }

            Log.Write("GPU preference: high-performance adapter unavailable; using adapter 0.");
        }
        catch (Exception exception)
        {
            Log.Write(
                "GPU preference detection failed; using adapter 0: " +
                exception.Message);
        }

        return 0;
    }
}
