using Android.Content;
using FleetWiseMobile.Services;

namespace FleetWiseMobile.Platforms.Android;

// Starts and stops the location tracking foreground service.
public class AndroidTripTracker : ITripTracker
{
    public void Start(string tripId)
    {
        var ctx = global::Android.App.Application.Context;
        var intent = new Intent(ctx, typeof(LocationTrackingService));
        intent.SetAction(LocationTrackingService.ActionStart);
        intent.PutExtra(LocationTrackingService.ExtraTripId, tripId);
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            ctx.StartForegroundService(intent);
        else
            ctx.StartService(intent);
    }

    public void Stop()
    {
        var ctx = global::Android.App.Application.Context;
        var intent = new Intent(ctx, typeof(LocationTrackingService));
        ctx.StopService(intent);
    }
}
