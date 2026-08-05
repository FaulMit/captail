using Windows.ApplicationModel;

namespace Captail;

internal sealed class StorePackageLifecycle : IDisposable
{
    private readonly Action<string> _requestShutdown;
    private PackageCatalog? _catalog;
    private int _stopping;

    private StorePackageLifecycle(Action<string> requestShutdown)
    {
        _requestShutdown = requestShutdown;
    }

    internal static StorePackageLifecycle? Start(
        Action<string> requestShutdown)
    {
        if (!AppDistribution.IsMicrosoftStore)
            return null;

        var listener = new StorePackageLifecycle(requestShutdown);
        try
        {
            listener._catalog = PackageCatalog.OpenForCurrentPackage();
            listener._catalog.PackageUninstalling +=
                listener.OnPackageUninstalling;
            listener._catalog.PackageUpdating += listener.OnPackageUpdating;
            return listener;
        }
        catch (Exception exception)
        {
            Log.Write(
                $"Could not monitor Store package lifecycle: {exception.Message}");
            listener.Dispose();
            return null;
        }
    }

    private void OnPackageUninstalling(
        PackageCatalog sender,
        PackageUninstallingEventArgs args) =>
        SignalStopping("uninstalling");

    private void OnPackageUpdating(
        PackageCatalog sender,
        PackageUpdatingEventArgs args) =>
        SignalStopping("updating");

    private void SignalStopping(string reason)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        Log.Write($"Store package is {reason}; stopping capture.");
        _requestShutdown(reason);
    }

    public void Dispose()
    {
        PackageCatalog? catalog = Interlocked.Exchange(ref _catalog, null);
        if (catalog is null)
            return;

        catalog.PackageUninstalling -= OnPackageUninstalling;
        catalog.PackageUpdating -= OnPackageUpdating;
    }
}
