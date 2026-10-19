using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using FleetWiseMobile.Models;
using FleetWiseMobile.Services;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Storage;

namespace FleetWiseMobile.Platforms.Android;

/// <summary>
/// Foreground service that keeps the GPS on for the whole trip and buffers telemetry rows.
/// </summary>
/// <remarks>
/// <para>The GPS reports about once a second. Which of those fixes become rows is decided
/// by <see cref="TelemetrySendRule"/>: every five seconds while moving, every thirty while
/// standing, and at once on pulling away or a passenger count change. Each row carries the
/// time its fix was taken, not the time it was written.</para>
///
/// <para>Listening continuously, rather than asking for a fresh fix every few seconds,
/// keeps the GPS warm, so each fix is current, and it takes fixes from the GPS alone. A
/// one-off request takes whichever source answers first, which is often a cell tower or
/// wifi fix tens of metres off.</para>
/// </remarks>
[Service(ForegroundServiceType = ForegroundService.TypeLocation, Exported = false)]
public class LocationTrackingService : Service
{
    public const string ActionStart = "fleetwise.action.START_TRACKING";
    public const string ExtraTripId = "trip_id";

    private const string ChannelId = "routesync_tracking";
    private const int NotifId = 7001;
    private const int IntervalMs = 1000;

    // A GPS fix older than this means the GPS has lost the sky, indoors or warming up.
    private static readonly TimeSpan GpsLostAfter = TimeSpan.FromSeconds(10);

    private System.Threading.Timer? _timer;
    private string _tripId = "";
    private bool _busy;

    private GpsListener? _gps;
    private TelemetryFix? _lastWritten;

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
        var tripId = intent?.GetStringExtra(ExtraTripId) ?? "";
        // A new trip starts its own run of rows, beginning with its first fix.
        if (tripId != _tripId)
            _lastWritten = null;
        _tripId = tripId;

        StartForegroundCompat();
        ListenToGps();

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
            NoteLocationBack();

            var countStr = await SecureStorage.Default.GetAsync($"trip_count_{_tripId}");
            // A missing key means the trip has ended or counting stopped, so logging stops
            // rather than writing a zero row that would corrupt the telemetry audit.
            if (string.IsNullOrEmpty(countStr)) return;
            int count = int.TryParse(countStr, out var c) ? c : 0;

            var row = FromGps(count) ?? await WithoutGps(count);
            if (row is null) return;

            await queue.EnqueueAsync(row.Value.Row);
            _lastWritten = row.Value.Fix;

            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
                await queue.FlushAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Track.Tick] {ex}"); }
        finally { _busy = false; }
    }

    // The newest GPS fix as a row, when the send rule wants it.
    private (PendingTelemetry Row, TelemetryFix Fix)? FromGps(int count)
    {
        var loc = LiveGpsFix();
        if (loc is null)
            return null;

        var fix = new TelemetryFix(
            PhTime.At(DateTimeOffset.FromUnixTimeMilliseconds(loc.Time)),
            loc.HasSpeed ? loc.Speed : null,
            loc.HasAccuracy ? loc.Accuracy : null,
            count);
        if (_lastWritten is not null && !TelemetrySendRule.Usable(fix))
            return null;
        if (!TelemetrySendRule.ShouldWrite(_lastWritten, fix))
            return null;

        return (Row(loc.Latitude, loc.Longitude, loc.HasBearing ? loc.Bearing : null, fix), fix);
    }

    // With no current GPS fix, the trip's first row, a count change and the standing
    // heartbeat still go out, from whatever one-off fix the phone can give. The fleet map
    // does not move a bus on such a fix unless it came from the GPS, but the count is kept
    // and the bus is not shown as silent.
    private async Task<(PendingTelemetry Row, TelemetryFix Fix)?> WithoutGps(int count)
    {
        if (LiveGpsFix() is not null)
            return null;
        if (_lastWritten is TelemetryFix was && count == was.Count
            && (PhTime.Now - was.At).TotalSeconds < TelemetrySendRule.StandingEverySeconds)
            return null;

        Microsoft.Maui.Devices.Sensors.Location? loc = null;
        try
        {
            loc = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(4)));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Track.GPS] {ex}"); }
        if (loc is null) return null;

        var fix = new TelemetryFix(PhTime.At(loc.Timestamp), loc.Speed, loc.Accuracy, count);
        return (Row(loc.Latitude, loc.Longitude, loc.Course, fix), fix);
    }

    // The newest GPS fix, or null when there is none or it is too old to say where the bus is.
    private global::Android.Locations.Location? LiveGpsFix()
    {
        var loc = _gps?.Latest;
        if (loc is null)
            return null;
        var ageMs = (SystemClock.ElapsedRealtimeNanos() - loc.ElapsedRealtimeNanos) / 1e6;
        return ageMs <= GpsLostAfter.TotalMilliseconds ? loc : null;
    }

    private PendingTelemetry Row(double lat, double lng, double? heading, TelemetryFix fix) => new()
    {
        TripId = _tripId,
        Latitude = lat,
        Longitude = lng,
        TotalPassengers = fix.Count,
        Speed = fix.Speed,
        Heading = heading,
        Accuracy = fix.Accuracy,
        Timestamp = fix.At
    };

    // Asks for a fix from the GPS about once a second for as long as the service runs.
    // Asking again, when the service is started again, replaces the earlier request.
    private void ListenToGps()
    {
        try
        {
            var manager = (LocationManager?)GetSystemService(LocationService);
            if (manager is null) return;
            _gps ??= new GpsListener();
            manager.RequestLocationUpdates(LocationManager.GpsProvider, IntervalMs, 0f, _gps, Looper.MainLooper);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Track.Listen] {ex}"); }
    }

    private sealed class GpsListener : Java.Lang.Object, ILocationListener
    {
        private volatile global::Android.Locations.Location? _latest;

        public global::Android.Locations.Location? Latest => _latest;

        public void OnLocationChanged(global::Android.Locations.Location location) => _latest = location;
        public void OnProviderDisabled(string provider) { }
        public void OnProviderEnabled(string provider) { }
        public void OnStatusChanged(string? provider, [GeneratedEnum] Availability status, Bundle? extras) { }
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
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetColor(unchecked((int)0xFF3AB39C))
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
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetColor(unchecked((int)0xFF3AB39C))
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
        if (_gps is not null)
        {
            try { ((LocationManager?)GetSystemService(LocationService))?.RemoveUpdates(_gps); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Track.Listen] {ex}"); }
        }
        base.OnDestroy();
    }
}
