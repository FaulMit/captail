using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.Activation;

namespace Captail;

internal static class AppDistribution
{
    internal const string StoreProductId = "9PKVNVLKPTPS";

#if MICROSOFT_STORE
    internal static bool IsMicrosoftStore { get; } = true;
#else
    internal static bool IsMicrosoftStore { get; } = HasPackageIdentity();
#endif

    internal static bool IsStartupTaskActivation()
    {
        if (!IsMicrosoftStore)
            return false;

        try
        {
            return Windows.ApplicationModel.AppInstance
                .GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (Exception exception)
        {
            Log.Write($"Could not read package activation: {exception.Message}");
            return false;
        }
    }

    private static bool HasPackageIdentity()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) ==
            ErrorInsufficientBuffer;
    }

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        StringBuilder? packageFullName);
}
