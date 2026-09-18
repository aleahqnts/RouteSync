using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services;

/// <summary>
/// Removes ghost trips from the shared database: active rows that neither a real driver
/// nor this build produced. Their signature is unambiguous:
///
///   trip_status = "Active"  AND  actual_start_time IS NULL  AND  is_simulated = false
///
/// • A real driver trip always stamps <c>actual_start_time</c> the moment it goes Active
///   (mobile <c>StartTripAsync</c> writes both in one PATCH), so a null start on an Active
///   trip can never be a legitimate trip.
/// • Rows tagged <c>is_simulated = true</c> were produced by the retired telemetry
///   simulator. They are excluded so that any left in the database are not mistaken for
///   the ghosts this service removes.
///
/// What remains matches exactly what an outdated build leaves on the shared database:
/// untagged active trips it created before the simulated tag and the operational-day
/// rollover existed.
///
/// Those rows stay active across days and reach both the fleet map, which filters on active
/// trips with no date bound, and the dashboard, which folds in trips still active from
/// yesterday. This service cannot stop the process writing them, but it can remove what
/// they leave behind on every sweep, so they are never on screen for long.
///
/// Telemetry is deleted first, then the trip.
/// </summary>
public class TripReaperService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    private readonly Supabase.Client _supabase;
    private readonly ILogger<TripReaperService> _logger;

    public TripReaperService(Supabase.Client supabase, ILogger<TripReaperService> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Trip reaper active: sweeping ghost trips every {Interval}.", SweepInterval);

        // Let the app finish starting before the first sweep.
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
                _logger.LogWarning(ex, "Trip reaper sweep failed; will retry next interval.");
            }
        }
        while (await HostedWait.ForAsync(SweepInterval, stoppingToken));
    }

    private async Task SweepAsync()
    {
        var ghosts = (await _supabase
            .From<Trip>()
            .Filter("trip_status", Operator.Equals, "Active")
            .Get()).Models
            .Where(t => !t.IsSimulated && t.ActualStartTime is null)
            .ToList();

        if (ghosts.Count == 0)
            return;

        foreach (var trip in ghosts)
        {
            // Telemetry is deleted before the trip, so no rows are orphaned regardless of
            // how the foreign keys are configured.
            await _supabase.From<TelemetryData>().Filter("trip_id", Operator.Equals, trip.TripId).Delete();
            await _supabase.From<Trip>().Filter("trip_id", Operator.Equals, trip.TripId).Delete();

            _logger.LogInformation(
                "Reaped ghost trip {TripId} (route {RouteId}, vehicle {VehicleId}, dated {Date:yyyy-MM-dd}) + its telemetry.",
                trip.TripId, trip.RouteId, trip.VehicleId, trip.Date);
        }

        _logger.LogInformation("Trip reaper removed {Count} ghost trip(s).", ghosts.Count);
    }
}
