using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using FleetWiseMobile.Models;
using FleetWiseMobile.Services;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Storage;

namespace FleetWiseMobile.Platforms.Android;

/// <summary>
/// Foreground service that polls GPS roughly every 5 seconds during a trip and buffers
/// telemetry rows.
/// </summary>
/// <remarks>
/// A row is written only when the bus has moved at least 25 metres, the passenger count
/// changed, or 60 seconds have passed. That keeps stored volume low without losing a
/// boarding recorded while the bus is stationary at a stop.
/// </remarks>
[Service(ForegroundServiceType = ForegroundService.TypeLocation, Exported = false)]
public class LocationTrackingService : Service
{
    public const string ActionStart = "fleetwise.action.START_TRACKING";
    public const string ExtraTripId = "trip_id";

    private const string ChannelId = "routesync_tracking";
    private const int NotifId = 7001;
    private const int IntervalMs = 5000;
    private const double MinMeters = 25.0;
    private const int HeartbeatSecs = 60;

    private System.Threading.Timer? _timer;
    private string _tripId = "";
    private bool _busy;

    private double? _lastLat, _lastLon;
    private int _lastCount = -1;
    private DateTime _lastWrite = DateTime.MinValue;

    private const string TrackingText = "Tracking your trip location";

    // Location switched off mid trip. The service would otherwise just stop getting
    // fixes and write nothing, so the bus leaves the fleet map with no sign on the
    // phone. It is said once per spell off, not on every tick.
    private bool _locationOff;
    private const string AlertChannelId = "routesync_location";
    // Negative, so it can never collide with a message notification, whose ids are
    // database identifiers masked to positive.
    private const int LocationOffNotifId = -7001;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        _tripId = intent?.GetStringExtra(ExtraTripId) ?? "";

        StartForegroundCompat();

        _timer ??= new System.Threading.Timer(_ => _ = Tick(), null, 0, IntervalMs);
        return StartCommandResult.Sticky; // OS restarts service if killed
    }

    private async Task Tick()
    {
        if (_busy || string.IsNullOrEmpty(_tripId)) return;
        _busy = true;
        try
        {
            var queue = IPlatformApplication.Current?.Services.GetService<TelemetryQueue>();
            if (queue is null) return;

            // Checked before asking for a fix. With location off the request fails
            // anyway, but by throwing, which says nothing about why.
            var guard = IPlatformApplication.Current?.Services.GetService<ILocationGuard>();
            if (guard is not null && !guard.ServicesEnabled())
            {
                NoteLocationOff();
                return;
            }

            Location? loc = null;
            try
            {
                loc = await Geolocation.Default.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(4)));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Track.GPS] {ex}"); }
            if (loc is null) return;
            NoteLocationBack();

            var countStr = await SecureStorage.Default.GetAsync($"trip_count_{_tripId}");
            // A missing key means the trip has ended or counting stopped, so logging stops
            // rather than writing a zero row that would corrupt the telemetry audit.
            if (string.IsNullOrEmpty(countStr)) return;
            int count = int.TryParse(countStr, out var c) ? c : 0;

            if (!ShouldWrite(loc.Latitude, loc.Longitude, count)) return;

            await queue.EnqueueAsync(new PendingTelemetry
            {
                TripId = _tripId,
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                TotalPassengers = count,
                Speed = loc.Speed,
                Heading = loc.Course,
                Timestamp = PhTime.Now
            });

            _lastLat = loc.Latitude;
            _lastLon = loc.Longitude;
            _lastCount = count;
            _lastWrite = DateTime.UtcNow;

            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
                await queue.FlushAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Track.Tick] {ex}"); }
        finally { _busy = false; }
    }

    private bool ShouldWrite(double lat, double lon, int count)
    {
        if (_lastLat is null) return true;                                   // first fix
        if (count != _lastCount) return true;                               // boarding (even at a stop)
        if ((DateTime.UtcNow - _lastWrite).TotalSeconds >= HeartbeatSecs) return true; // heartbeat
        var meters = Location.CalculateDistance(_lastLat.Value, _lastLon!.Value, lat, lon,
            DistanceUnits.Kilometers) * 1000.0;
        return meters >= MinMeters;                                         // moved enough
    }

    private void StartForegroundCompat()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var mgr = (NotificationManager)GetSystemService(NotificationService)!;
            var channel = new NotificationChannel(ChannelId, "Trip Tracking", NotificationImportance.Low);
            mgr.CreateNotificationChannel(channel);
        }

        var notif = TrackingNotification(TrackingText);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotifId, notif, ForegroundService.TypeLocation);
        else
            StartForeground(NotifId, notif);
    }

    private Notification TrackingNotification(string text) =>
        new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("RouteSync")
            .SetContentText(text)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true)
            .Build();

    private void NoteLocationOff()
    {
        if (_locationOff) return;
        _locationOff = true;

        try
        {
            // The standing tracking notification says it too, so the fact is still in
            // the shade after the alert below has been swiped away.
            NotificationManagerCompat.From(this).Notify(NotifId,
                TrackingNotification("Location is off. Dispatch cannot see this bus."));

            // Its own channel at high importance, not the messages channel, so a driver
            // who has muted messages still hears this. The tracking channel is low
            // importance by design and would not surface it.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var mgr = (NotificationManager)GetSystemService(NotificationService)!;
                mgr.CreateNotificationChannel(new NotificationChannel(
                    AlertChannelId, "Location alerts", NotificationImportance.High));
            }

            // Tapping goes straight to the location switch rather than into the app,
            // because that is the one thing the driver needs to do.
            var settings = new Intent(global::Android.Provider.Settings.ActionLocationSourceSettings);
            settings.AddFlags(ActivityFlags.NewTask);
            var pending = PendingIntent.GetActivity(this, 1, settings,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            const string body = "This phone has stopped sharing its location, so the bus no " +
                "longer appears on the fleet map. Please turn location back on.";

            var alert = new NotificationCompat.Builder(this, AlertChannelId)
                .SetContentTitle("Location Is Off")
                .SetContentText(body)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
                .SetSmallIcon(Resource.Mipmap.appicon)
                .SetPriority(NotificationCompat.PriorityHigh)
                .SetAutoCancel(true)
                .SetContentIntent(pending)
                .Build();

            NotificationManagerCompat.From(this).Notify(LocationOffNotifId, alert);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Track.LocationOff] {ex}"); }
    }

    private void NoteLocationBack()
    {
        if (!_locationOff) return;
        _locationOff = false;

        try
        {
            NotificationManagerCompat.From(this).Notify(NotifId, TrackingNotification(TrackingText));
            // The alert is withdrawn once it is no longer true.
            NotificationManagerCompat.From(this).Cancel(LocationOffNotifId);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Track.LocationBack] {ex}"); }
    }

    public override void OnDestroy()
    {
        _timer?.Dispose();
        _timer = null;
        base.OnDestroy();
    }
}
