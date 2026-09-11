using Android.Content;
using Android.Locations;
using Android.OS;
using FleetWiseMobile.Services;

namespace FleetWiseMobile.Platforms.Android;

/// <summary>Reads the location switch and the app's permission from the platform.</summary>
public class AndroidLocationGuard : ILocationGuard
{
    public async Task<LocationState> CheckAsync()
    {
        try
        {
            var perm = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (perm != PermissionStatus.Granted) return LocationState.PermissionDenied;
        }
        catch (Exception ex)
        {
            // A failed read is not evidence of a denial. Reporting one would put a
            // warning in front of a driver whose phone is working.
            System.Diagnostics.Debug.WriteLine($"[LocationGuard.Perm] {ex}");
        }

        return ServicesEnabled() ? LocationState.Ready : LocationState.ServicesOff;
    }

    public bool ServicesEnabled()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            if (ctx.GetSystemService(Context.LocationService) is not LocationManager lm)
                return true;

            // One switch since Android 9. Before it, location counts as on when any
            // provider that can produce a fix is enabled, which includes the battery
            // saving mode that uses the network alone.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                return lm.IsLocationEnabled;

            return lm.IsProviderEnabled(LocationManager.GpsProvider)
                || lm.IsProviderEnabled(LocationManager.NetworkProvider);
        }
        catch (Exception ex)
        {
            // Same reasoning as above: an unreadable switch is assumed on rather than
            // raising an alarm the driver can do nothing about.
            System.Diagnostics.Debug.WriteLine($"[LocationGuard.Services] {ex}");
            return true;
        }
    }

    public void OpenLocationSettings()
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionLocationSourceSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocationGuard.Open] {ex}");
            // Some builds do not expose the location screen directly. The app's own page
            // is the nearest place that still leads there.
            OpenAppSettings();
        }
    }

    public void OpenAppSettings()
    {
        try { AppInfo.Current.ShowSettingsUI(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LocationGuard.App] {ex}"); }
    }
}
