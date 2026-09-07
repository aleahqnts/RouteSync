using System.Net.Http;
using System.Text;
using System.Text.Json;
using FleetWiseMobile.Models;
using SQLite;

namespace FleetWiseMobile.Services;

/// <summary>
/// On-device buffer for GPS telemetry.
/// </summary>
/// <remarks>
/// Rows are written locally first, so they survive dead zones and the app being killed.
/// A flush loop then posts them to the database and deletes the local copy once the
/// write succeeds.
/// </remarks>
public class TelemetryQueue
{
    private readonly SQLiteAsyncConnection _db;
    private static readonly HttpClient _http = new();
    private static readonly SemaphoreSlim _flushLock = new(1, 1);

    public TelemetryQueue()
    {
        var path = Path.Combine(FileSystem.AppDataDirectory, "telemetry.db3");
        _db = new SQLiteAsyncConnection(path);
        _db.CreateTableAsync<PendingTelemetry>().Wait();
        _db.CreateTableAsync<PendingTripFinalize>().Wait();
    }

    public Task EnqueueAsync(PendingTelemetry row) => _db.InsertAsync(row);

    /// <summary>Records that a trip has ended, replacing any earlier record of the
    /// same trip ending.</summary>
    /// <remarks>
    /// One row per trip. A driver who ends a trip, is offered it again because the
    /// first end has not reached the server, and ends it a second time used to leave two
    /// rows, applied in insertion order, so the last and least informed write won. The
    /// second end is the less informed of the two: ending clears the local count, so the
    /// figure it carries is whatever the server was holding rather than what was counted.
    ///
    /// The surviving row keeps the higher count for the same reason the database does.
    /// Counting only rises, so a lower figure is a staler one.
    /// </remarks>
    public async Task EnqueueFinalizeAsync(PendingTripFinalize f)
    {
        var existing = await _db.Table<PendingTripFinalize>()
            .Where(r => r.TripId == f.TripId).ToListAsync();

        if (existing.Count > 0)
        {
            f.TotalBoarded = Math.Max(f.TotalBoarded, existing.Max(r => r.TotalBoarded));
            var ids = existing.Select(r => r.Id).ToList();
            await _db.Table<PendingTripFinalize>().DeleteAsync(r => ids.Contains(r.Id));
        }
        await _db.InsertAsync(f);
    }

    /// <summary>Whether this trip has already been ended on this phone.</summary>
    /// <remarks>
    /// The queue is what makes an offline end a fact rather than an intention, so it is
    /// asked before the server is believed about a trip still being active.
    /// </remarks>
    public async Task<bool> HasFinalizeAsync(string tripId) =>
        await _db.Table<PendingTripFinalize>().Where(r => r.TripId == tripId).CountAsync() > 0;

    public Task<int> CountAsync() => _db.Table<PendingTelemetry>().CountAsync();

    /// <summary>Pushes buffered rows in batches, stopping at the first network failure.
    /// Anything not sent stays queued for the next flush.</summary>
    public async Task FlushAsync()
    {
        if (!await _flushLock.WaitAsync(0)) return; // a flush is already running
        try
        {
            await FlushFinalizesAsync(); // push trip totals first (audit), then GPS

            while (true)
            {
                var batch = await _db.Table<PendingTelemetry>()
                    .OrderBy(r => r.Id).Take(50).ToListAsync();
                if (batch.Count == 0) return;

                var body = batch.Select(r => new
                {
                    trip_id = r.TripId,
                    latitude = r.Latitude,
                    longitude = r.Longitude,
                    total_passengers = r.TotalPassengers,
                    speed = r.Speed,
                    heading = r.Heading,
                    timestamp = r.Timestamp
                });

                var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{SupabaseConfig.Url}/rest/v1/telemetry_data");
                req.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseConfig.Bearer}");
                req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode) return; // keep rows, retry later

                var ids = batch.Select(r => r.Id).ToList();
                await _db.Table<PendingTelemetry>().DeleteAsync(r => ids.Contains(r.Id));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TelemetryQueue.Flush] {ex}");
        }
        finally { _flushLock.Release(); }
    }

    /// <summary>Pushes queued trip finalizations, marking each trip completed and writing
    /// its authoritative totals.</summary>
    private async Task FlushFinalizesAsync()
    {
        var fins = await _db.Table<PendingTripFinalize>().OrderBy(f => f.Id).ToListAsync();
        foreach (var f in fins)
        {
            // total_boarded is sent as a claim. A trigger keeps the high-water mark, so
            // this cannot lower a count the counter phone made while this app was out of
            // contact, and estimated_revenue is re-derived from whichever figure wins.
            var body = new
            {
                trip_status = "Completed",
                total_boarded = f.TotalBoarded,
                boarded_adjustment = f.BoardedAdjustment,
                estimated_revenue = f.Revenue,
                actual_end_time = f.EndTime
            };
            var req = new HttpRequestMessage(HttpMethod.Patch,
                $"{SupabaseConfig.Url}/rest/v1/trips?trip_id=eq.{Uri.EscapeDataString(f.TripId)}");
            req.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseConfig.Bearer}");
            req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return; // keep, retry later

            // Release the bus. With the trip completed, vehicle_status has to leave "On
            // Trip" or the row stays stuck: the dashboard derives Ready as a fallback, but
            // other consumers read the raw column.
            //
            // The order is required. The row-level security policy that lets a driver
            // update this vehicle checks for a completed trip of theirs, so that trip must
            // already be written.
            //
            // Best effort: a failure here does not re-queue the finalization, which has
            // already succeeded.
            if (!string.IsNullOrEmpty(f.VehicleId))
            {
                var vBody = new { vehicle_status = "Ready to Deploy", updated_at = f.EndTime };
                var vReq = new HttpRequestMessage(HttpMethod.Patch,
                    $"{SupabaseConfig.Url}/rest/v1/vehicles?vehicle_id=eq.{Uri.EscapeDataString(f.VehicleId)}&vehicle_status=eq.On%20Trip");
                vReq.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
                vReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseConfig.Bearer}");
                vReq.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
                vReq.Content = new StringContent(JsonSerializer.Serialize(vBody), Encoding.UTF8, "application/json");
                await _http.SendAsync(vReq); // ignore result: trip finalize already landed
            }

            await _db.DeleteAsync(f);
        }
    }
}
