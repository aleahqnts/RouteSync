using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services
{
    /// <summary>
    /// Closes trips that were started but never ended.
    /// </summary>
    /// <remarks>
    /// A driver who finishes a route without pressing End Trip leaves the trip active
    /// indefinitely. It keeps its bus marked as on trip, keeps appearing on the fleet map
    /// and the dispatch board, and its duration grows without limit.
    ///
    /// The cutoff is measured from the shift's scheduled end rather than from the start,
    /// so a long shift is judged by when it was meant to finish. A full day past that is
    /// deliberately generous: an overnight shift already ends the following morning, and a
    /// driver who is simply late must never have their trip closed underneath them.
    ///
    /// The recorded end is the scheduled end, not the moment this runs. Writing the current
    /// time would claim a trip ran for as long as nobody noticed, which is exactly the
    /// figure this service exists to prevent.
    /// </remarks>
    public class StaleTripCloserService : BackgroundService
    {
        /// <summary>How far past its scheduled end a trip may run before it is closed.</summary>
        private static readonly TimeSpan Grace = TimeSpan.FromHours(24);

        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

        private readonly Supabase.Client _supabase;
        private readonly ILogger<StaleTripCloserService> _logger;

        public StaleTripCloserService(Supabase.Client supabase, ILogger<StaleTripCloserService> logger)
        {
            _supabase = supabase;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "Stale trip closer active: trips are closed {Hours}h past their scheduled end, swept every {Interval}.",
                Grace.TotalHours, SweepInterval);

            if (!await HostedWait.ForAsync(StartupDelay, stoppingToken)) return;

            do
            {
                try
                {
                    await SweepAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // A transient failure must not end the loop, so it is logged and retried.
                    _logger.LogWarning(ex, "Stale trip sweep failed; will retry next interval.");
                }
            }
            while (await HostedWait.ForAsync(SweepInterval, stoppingToken));
        }

        private async Task SweepAsync()
        {
            var now = PhClock.Now;

            // Only trips that genuinely started. One that is active with no start time is a
            // ghost, which TripReaperService deletes rather than closes.
            var stale = (await _supabase
                .From<Trip>()
                .Filter("trip_status", Operator.Equals, "Active")
                .Get()).Models
                .Where(t => t.ActualStartTime is not null && now > ScheduledEnd(t) + Grace)
                .ToList();

            if (stale.Count == 0)
                return;

            foreach (var trip in stale)
            {
                var end = ScheduledEnd(trip);

                await _supabase.From<Trip>()
                    .Filter("trip_id", Operator.Equals, trip.TripId)
                    .Set(t => t.TripStatus, "Completed")
                    .Set(t => t.ActualEndTime, end)
                    .Update();

                // Release the bus, or it stays marked as on trip forever. Filtered on the
                // current value so a bus already reassigned elsewhere is left alone.
                if (!string.IsNullOrWhiteSpace(trip.VehicleId))
                {
                    await _supabase.From<Vehicle>()
                        .Filter("vehicle_id", Operator.Equals, trip.VehicleId)
                        .Filter("vehicle_status", Operator.Equals, "On Trip")
                        .Set(v => v.VehicleStatus, "Ready to Deploy")
                        .Update();
                }

                _logger.LogInformation(
                    "Closed stale trip {TripId} (bus {VehicleId}, driver {DriverId}) scheduled to end {End:yyyy-MM-dd HH:mm}, {Hours:F0}h overdue.",
                    trip.TripId, trip.VehicleId, trip.DriverId, end, (now - end).TotalHours);
            }

            _logger.LogInformation("Stale trip closer completed {Count} forgotten trip(s).", stale.Count);
        }

        /// <summary>
        /// When the shift was meant to finish, on the trip's own date. An end at or before
        /// the start means the shift runs overnight, so it lands on the following day.
        /// </summary>
        private static DateTime ScheduledEnd(Trip t) =>
            t.Date.Date + t.ShiftEndTime +
            (t.ShiftEndTime <= t.ShiftStartTime ? TimeSpan.FromDays(1) : TimeSpan.Zero);
    }
}
