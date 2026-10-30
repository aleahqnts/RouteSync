using Android.Content;
using Android.Provider;
using FleetWiseMobile.Services;

namespace FleetWiseMobile.Platforms.Android;

// Installs a downloaded APK through the phone's own package installer.
//
// Android always asks the driver to confirm an update that did not come from the Play
// Store. The file is shared through MAUI's FileProvider, which already covers the cache
// folder the download is saved in.
public class AndroidApkInstaller : IApkInstaller
{
    public bool EnsureAllowed()
    {
        var context = Platform.AppContext;
        if (context.PackageManager!.CanRequestPackageInstalls()) return true;

        var settings = new Intent(Settings.ActionManageUnknownAppSources,
            global::Android.Net.Uri.Parse("package:" + context.PackageName));
        settings.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(settings);
        return false;
    }

    public void Install(string apkPath)
    {
        var context = Platform.AppContext;
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, context.PackageName + ".fileProvider", new Java.IO.File(apkPath));

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
        context.StartActivity(intent);
    }
}
