using System.Net.Http;
using System.Text;
using System.Text.Json;
using FleetWiseMobile.Models;
using static Postgrest.Constants;

namespace FleetWiseMobile.Services;

/// <summary>
/// Every database read and write the driver app performs.
/// </summary>
/// <remarks>
/// Uses the same tables and status strings as the web dashboard's dispatch controller, so
/// the two surfaces agree on what a trip looks like.
/// </remarks>
public class DriverDataService
{
    private readonly Supabase.Client _supabase;

    public DriverDataService(Supabase.Client supabase) => _supabase = supabase;

    // Twenty seconds, not the hundred a HttpClient starts with. A phone that has drifted
    // out of signal holds the socket open with nothing coming back, and every page that
    // waits on one of these shows a skeleton for as long as it waits.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Sends a PATCH straight to the REST endpoint.</summary>
    /// <remarks>
    /// The postgrest client's expression-based Update does not work on Android, and its
    /// full-model Upsert round-trips the whole row, which corrupts the `date` column.
    /// </remarks>
    private static async Task PatchAsync(string pathWithFilter, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"{FleetWiseMobile.SupabaseConfig.Url}/rest/v1/{pathWithFilter}");
        req.Headers.TryAddWithoutValidation("apikey", FleetWiseMobile.SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FleetWiseMobile.SupabaseConfig.Bearer}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await _http.SendAsync(req);
        await ThrowIfRefusedAsync(res);
    }

    /// <summary>
    /// Sends a PATCH and reads back the rows it actually changed.
    /// </summary>
    /// <remarks>
    /// A PATCH whose filter matches nothing answers 204 and succeeds. That is the right
    /// answer to "make these rows look like this" and the wrong one to "cancel this
    /// request", which has not happened. Asking for the rows back is what tells the two
    /// apart, so a write that quietly changed nothing can be reported rather than passed
    /// off as done.
    /// </remarks>
    private static async Task<int> PatchCountingAsync(string pathWithFilter, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"{FleetWiseMobile.SupabaseConfig.Url}/rest/v1/{pathWithFilter}");
        req.Headers.TryAddWithoutValidation("apikey", FleetWiseMobile.SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FleetWiseMobile.SupabaseConfig.Bearer}");
        req.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var res = await _http.SendAsync(req);
        await ThrowIfRefusedAsync(res);

        var body_ = await res.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body_);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 1;
        }
        catch { return 0; }
    }

    /// <summary>Sends an insert straight to the REST endpoint, for the same reason as
    /// <see cref="PatchAsync"/>.</summary>
    private static async Task PostAsync(string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{FleetWiseMobile.SupabaseConfig.Url}/rest/v1/{path}");
        req.Headers.TryAddWithoutValidation("apikey", FleetWiseMobile.SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FleetWiseMobile.SupabaseConfig.Bearer}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await _http.SendAsync(req);
        await ThrowIfRefusedAsync(res);
    }

    /// <summary>Fails with what the server said, rather than with a status code.</summary>
    /// <remarks>
    /// EnsureSuccessStatusCode throws away the body, which is where postgrest puts the
    /// reason: a column that does not exist, a check constraint, a row the key may not
    /// write. Without it every refusal reaches the driver as the same sentence about
    /// their connection, and sends them looking in the wrong place.
    /// </remarks>
    private static async Task ThrowIfRefusedAsync(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;

        var body = "";
        try { body = await res.Content.ReadAsStringAsync(); } catch { }

        // postgrest answers with { code, message, details, hint }. The message is the
        // readable part; the rest is for a log, not a phone.
        var reason = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m))
                reason = m.GetString() ?? body;
        }
        catch { }

        if (string.IsNullOrWhiteSpace(reason))
            reason = $"The server refused it ({(int)res.StatusCode}).";

        throw new HttpRequestException(reason, null, res.StatusCode);
    }

    /// <summary>Reads the first row from the REST endpoint, or null when there is none.</summary>
    /// <remarks>
    /// `device_config` and `device_status` are plain data transfer objects rather than
    /// postgrest models, so they do not need a model base class.
    /// </remarks>
    private static async Task<T?> GetJsonAsync<T>(string pathWithQuery)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{FleetWiseMobile.SupabaseConfig.Url}/rest/v1/{pathWithQuery}");
        req.Headers.TryAddWithoutValidation("apikey", FleetWiseMobile.SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FleetWiseMobile.SupabaseConfig.Bearer}");
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        var list = JsonSerializer.Deserialize<List<T>>(json);
        return list is { Count: > 0 } ? list[0] : default;
    }

    // Remote camera control. Row-level security scopes both tables to the camera on the
    // driver's active trip, so with no trip running these reads return nothing.

    public Task<DeviceConfigDto?> GetDeviceConfigAsync(string deviceId)
        => GetJsonAsync<DeviceConfigDto>($"device_config?device_id=eq.{Uri.EscapeDataString(deviceId)}");

    public Task<DeviceStatusDto?> GetDeviceStatusAsync(string deviceId)
        => GetJsonAsync<DeviceStatusDto>($"device_status?device_id=eq.{Uri.EscapeDataString(deviceId)}");

    public Task PatchDeviceConfigAsync(string deviceId, object body)
        => PatchAsync($"device_config?device_id=eq.{Uri.EscapeDataString(deviceId)}", body);

    /// <summary>
    /// Downloads the camera's most recent wake snapshot, or null when there is none.
    /// </summary>
    /// <remarks>
    /// An authenticated storage read. Row-level security serves only the object belonging
    /// to the active trip's camera, so null covers a purged image, one not captured yet,
    /// and a denied request alike.
    ///
    /// The timestamp query parameter is required, not decorative. The object is
    /// overwritten in place on every wake and storage caches by URL, so without a unique
    /// query string a refresh returns the previous photo.
    /// </remarks>
    public async Task<byte[]?> DownloadSnapshotAsync(string deviceId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"{FleetWiseMobile.SupabaseConfig.Url}/storage/v1/object/authenticated/camera-snapshots/{Uri.EscapeDataString(deviceId)}.jpg?t={DateTime.UtcNow.Ticks}");
        req.Headers.TryAddWithoutValidation("apikey", FleetWiseMobile.SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FleetWiseMobile.SupabaseConfig.Bearer}");
        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadAsByteArrayAsync();
    }

    public async Task<string> GetAvailabilityAsync(int userId)
    {
        var r = await _supabase.From<DriverAvailability>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Get();
        return r.Models.FirstOrDefault()?.AvailabilityStatus ?? "Unavailable";
    }

    public async Task SetAvailabilityAsync(int userId, string status, string? reason = null)
    {
        // Reads through the postgrest client are reliable, but writes go through the REST
        // endpoint like every other write in this service. Its Insert and Upsert fail
        // silently on MAUI, which left new drivers unable to set themselves available.
        var existing = await _supabase.From<DriverAvailability>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Get();

        if (existing.Models.Any())
        {
            await PatchAsync($"driver_availability?user_id=eq.{userId}",
                new { availability_status = status, reason, updated_at = PhTime.Now });
        }
        else
        {
            await PostAsync("driver_availability",
                new { user_id = userId, availability_status = status, reason, updated_at = PhTime.Now });
        }
    }

    /// <summary>The driver's current assignment: any trip today that is not yet completed.</summary>
    public async Task<Trip?> GetTodayAssignmentAsync(int userId)
    {
        // Yesterday is included so an overnight shift, for example 10pm to 6am, is still
        // found after midnight even though it belongs to the previous calendar day.
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("date", Operator.GreaterThanOrEqual, yesterday)
            .Filter("date", Operator.LessThanOrEqual, DateTime.Today.ToString("yyyy-MM-dd"))
            .Get();

        // A shift that ended without ever being started is dropped. One that is Active
        // stays even past its end time, because the driver is still on it.
        var now = PhTime.Now;
        return r.Models
            .Where(t => t.TripStatus != "Completed")
            .Where(t => t.TripStatus == "Active" || now < ShiftEnd(t))
            .OrderBy(t => t.Date).ThenBy(t => t.ShiftStartTime)
            .FirstOrDefault();
    }

    /// <summary>Wall-clock end of a shift. An end at or before the start means the shift
    /// runs overnight, so it rolls to the next day.</summary>
    private static DateTime ShiftEnd(Trip t)
        => t.Date.Date + t.ShiftEndTime + (t.ShiftEndTime <= t.ShiftStartTime ? TimeSpan.FromDays(1) : TimeSpan.Zero);

    /// <summary>The nearest trip after today that is not completed, shown as a preview on
    /// the home screen.</summary>
    public async Task<Trip?> GetUpcomingAssignmentAsync(int userId)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("date", Operator.GreaterThan, today)
            .Order("date", Ordering.Ascending)
            .Get();

        return r.Models
            .Where(t => t.TripStatus != "Completed")
            .OrderBy(t => t.Date).ThenBy(t => t.ShiftStartTime)
            .FirstOrDefault();
    }

    public async Task<BusRoute?> GetRouteAsync(int routeId)
    {
        var r = await _supabase.From<BusRoute>()
            .Filter("route_id", Operator.Equals, routeId.ToString())
            .Get();
        return r.Models.FirstOrDefault();
    }

    public async Task<Vehicle?> GetVehicleAsync(string vehicleId)
    {
        var r = await _supabase.From<Vehicle>()
            .Filter("vehicle_id", Operator.Equals, vehicleId)
            .Get();
        return r.Models.FirstOrDefault();
    }

    /// <summary>The most recent checklist submitted for a trip, or null if there is none.</summary>
    /// <summary>The most recent inspection for a trip, optionally narrowed to one bus.</summary>
    /// <remarks>
    /// A trip can carry more than one inspection, because a bus reassigned after the
    /// first one has not been looked at. Passing the vehicle asks the question that
    /// matters before a shift starts, which is whether the bus about to be driven has
    /// been inspected, not whether the trip has an inspection somewhere in its history.
    /// Omitting it returns the trip's latest whatever bus it describes, which is what a
    /// log of what happened wants.
    /// </remarks>
    public async Task<BusChecklist?> GetChecklistAsync(string tripId, string? vehicleId = null)
    {
        var q = _supabase.From<BusChecklist>()
            .Filter("trip_id", Operator.Equals, tripId);
        if (!string.IsNullOrEmpty(vehicleId))
            q = q.Filter("vehicle_id", Operator.Equals, vehicleId);

        var r = await q.Get();
        return r.Models.OrderByDescending(c => c.SubmittedAt).FirstOrDefault();
    }

    /// <summary>What the bus is doing before a shift on it can be started.</summary>
    /// <remarks>
    /// Whether the bus still carries an open trip, when that trip was rostered to end, and
    /// whether it has been inspected in this operational day. All three are facts about
    /// somebody else's work, which a driver key cannot read: trips are scoped to their own
    /// driver and inspections to the trips they belong to.
    ///
    /// Asked of a function rather than of the tables, so neither policy has to be widened
    /// to serve a screen that needs a yes or a no. The function answers only for a trip
    /// this driver is on, and names nobody.
    ///
    /// Null when the answer cannot be had. Both features it feeds then stay hidden, which
    /// holds the driver to the ordinary start window and the full inspection.
    /// </remarks>
    public Task<ShiftStartContext?> GetShiftStartContextAsync(string tripId)
        => RpcReadAsync<ShiftStartContext>("shift_start_context", new { p_trip_id = tripId });

    /// <summary>Records that a trip stood on the inspection its bus already cleared today.</summary>
    /// <remarks>
    /// A row rather than nothing at all, because every trip has to carry an inspection of
    /// its own for the dashboard to read and for the shift to be startable. The status is
    /// what says nobody walked around the bus a second time, and there are no item
    /// results because none were taken.
    ///
    /// Written straight to the table rather than through the inspection function, whose
    /// work is judging item results and grounding a bus on a critical fault. A skip
    /// judges nothing, so there is nothing for it to decide.
    /// </remarks>
    public async Task SkipChecklistAsync(string tripId, string vehicleId, int driverId)
    {
        // The five sections are required columns, and a skip has nothing to put in them.
        // Left out, the database refused every skip on the missing values. An empty
        // section is the truth of an inspection that was not carried out, and it reads
        // back as a section with no items everywhere a checklist is shown.
        var none = new Dictionary<string, string>();
        await PostAsync("bus_checklist", new
        {
            trip_id = tripId,
            vehicle_id = vehicleId,
            driver_id = driverId,
            submitted_at = PhTime.Now,
            checklist_status = "Skipped",
            exterior_inspection = none,
            engine_compartment = none,
            interior_inspection = none,
            brake_safety = none,
            passenger_systems = none,
        });
    }

    public async Task<Trip?> GetLastCompletedTripAsync(int userId)
    {
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("trip_status", Operator.Equals, "Completed")
            .Get();
        return r.Models.OrderByDescending(t => t.Date).FirstOrDefault();
    }

    public async Task<Trip?> GetTripAsync(string tripId)
    {
        var r = await _supabase.From<Trip>()
            .Filter("trip_id", Operator.Equals, tripId)
            .Get();
        return r.Models.FirstOrDefault();
    }

    public async Task<List<Trip>> GetTripsForDriverAsync(int userId)
    {
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("trip_status", Operator.Equals, "Completed")
            .Order("date", Ordering.Descending)
            .Get();
        return r.Models;
    }

    /// <summary>
    /// Messages this driver should see: broadcasts, messages for any route they run, and
    /// messages addressed to them directly.
    /// </summary>
    /// <remarks>
    /// Limited to the last 14 days to keep the history small. Message volume is low
    /// enough that route and driver matching is resolved on the device.
    /// </remarks>
    public async Task<List<MessageModel>> GetMessagesAsync(int userId)
    {
        var cutoff = PhTime.Now.AddDays(-14);

        // Messages sent before the account existed are never shown. Broadcasts match
        // every driver and route messages match any route they are assigned to, so
        // without this clamp a new driver would inherit the entire 14-day backlog.
        var user = await GetUserAsync(userId);
        if (user is not null && user.CreatedAt > cutoff) cutoff = user.CreatedAt;

        // Route identifiers this driver has ever run. A small set.
        var trips = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Get();
        var myRoutes = trips.Models
            .Select(t => t.RouteId.ToString())
            .ToHashSet();

        var r = await _supabase.From<MessageModel>()
            .Filter("created_at", Operator.GreaterThanOrEqual, cutoff.ToString("yyyy-MM-dd HH:mm:ss"))
            .Order("created_at", Ordering.Descending)
            .Get();

        var me = userId.ToString();
        return r.Models.Where(m => (m.TargetAudience ?? "").ToLowerInvariant() switch
        {
            "all"    => true,
            "route"  => myRoutes.Contains(m.TargetId),
            "driver" => m.TargetId == me,
            _        => false
        }).ToList();
    }

    /// <summary>Read state, which is meaningful only for messages addressed to a single
    /// driver.</summary>
    public async Task MarkMessageReadAsync(long id)
        => await PatchAsync($"messages?message_id=eq.{id}", new { is_read = true });

    public async Task<UserModel?> GetUserAsync(int userId)
    {
        var r = await _supabase.From<UserModel>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Get();
        return r.Models.FirstOrDefault();
    }

    public async Task<UserModel?> GetDriverByEmailAsync(string email)
    {
        var r = await _supabase.From<UserModel>()
            .Filter("email_address", Operator.Equals, email)
            .Get();
        var u = r.Models.FirstOrDefault();
        return (u is not null && u.RoleId == 2 && u.AccountStatus == "Activated") ? u : null;
    }

    // Profile writes go through the users_app view, which exposes no password hash column
    // and restricts an authenticated caller to their own row.
    public async Task StampLoginAsync(int userId)
        => await PatchAsync($"users_app?user_id=eq.{userId}", new { last_login = PhTime.Now });

    public async Task UpdateProfileAsync(int userId, string? contact, string? address, string? emName, string? emNumber)
        => await PatchAsync($"users_app?user_id=eq.{userId}", new
        {
            contact_number = contact,
            address = address,
            emergency_contact_name = emName,
            emergency_contact_number = emNumber,
            updated_at = PhTime.Now
        });

    // Outside the web dashboard, the change-password edge function is the only writer of
    // password_hash. The app never touches it.


    /// <summary>The inspection items to show, in the order the dashboard set.</summary>
    public async Task<List<ChecklistItem>> GetChecklistItemsAsync()
    {
        var r = await _supabase.From<ChecklistItem>()
            .Filter("active", Operator.Equals, "true")
            .Order("sort_order", Postgrest.Constants.Ordering.Ascending)
            .Get();
        return r.Models;
    }

    public async Task UpdateVehicleStatusAsync(string vehicleId, string status)
    {
        await PatchAsync($"vehicles?vehicle_id=eq.{Uri.EscapeDataString(vehicleId)}",
            new { vehicle_status = status, updated_at = PhTime.Now });
    }

    public async Task<decimal> GetFareAsync()
    {
        var r = await _supabase.From<FareConfig>()
            .Filter("id", Operator.Equals, "1")
            .Get();
        return r.Models.FirstOrDefault()?.StandardFare ?? 0m;
    }

    // Trip writes use a column-targeted REST PATCH, which works on Android and leaves the
    // `date` column alone.
    public async Task StartTripAsync(string tripId)
    {
        var t = await GetTripAsync(tripId);
        if (t is null) return;

        object body;
        if (t.TripStatus != "Active")
            body = new { trip_status = "Active", actual_start_time = PhTime.Now }; // fresh start
        else
            body = new { trip_status = "Active" }; // resume keeps original start

        await PatchAsync($"trips?trip_id=eq.{Uri.EscapeDataString(tripId)}", body);

        if (!string.IsNullOrEmpty(t.VehicleId))
            await UpdateVehicleStatusAsync(t.VehicleId, "On Trip");
    }

    /// <summary>Pushes the running count.</summary>
    /// <remarks>
    /// The count is a claim rather than a value. While a camera is counting, a trigger
    /// keeps the higher of this and what is stored, so a stale write from this app
    /// cannot replace a count the counter phone made in a dead zone. While no camera is
    /// counting the driver owns the figure and this sets it, which is what makes the
    /// manual minus button work.
    /// </remarks>
    public async Task UpdateTripProgressAsync(string tripId, int totalBoarded, decimal revenue)
    {
        await PatchAsync($"trips?trip_id=eq.{Uri.EscapeDataString(tripId)}",
            new { total_boarded = totalBoarded, estimated_revenue = revenue });
    }

    public async Task EndTripAsync(string tripId, int totalBoarded, decimal revenue)
    {
        var t = await GetTripAsync(tripId);

        await PatchAsync($"trips?trip_id=eq.{Uri.EscapeDataString(tripId)}",
            new
            {
                trip_status = "Completed",
                total_boarded = totalBoarded,
                estimated_revenue = revenue,
                actual_end_time = PhTime.Now
            });

        if (!string.IsNullOrEmpty(t?.VehicleId))
            await UpdateVehicleStatusAsync(t.VehicleId, "Ready to Deploy");
    }

    // --- Leave -------------------------------------------------------------

    /// <summary>Everything this driver has ever filed, newest first.</summary>
    /// <remarks>
    /// The whole history rather than a window, because the balance is worked out from it
    /// and a year's requests is a handful of rows.
    /// </remarks>
    public async Task<List<LeaveRequest>> GetLeaveRequestsAsync(int userId)
    {
        var r = await _supabase.From<LeaveRequest>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Order("filed_at", Ordering.Descending)
            .Get();
        return r.Models;
    }

    /// <summary>Files a request, which starts out waiting on a decision.</summary>
    /// <remarks>
    /// Three days' notice is asked for and never required, so nothing here refuses a
    /// request for being late. filed_at is left to the database default so the time of
    /// filing is the server's, not a phone's clock that may be wrong.
    ///
    /// Sent as a plain insert for the same reason the rest of this class does: the
    /// postgrest client's model round-trip corrupts date columns on Android.
    /// </remarks>
    public async Task FileLeaveAsync(
        int userId, string leaveType, DateTime startDate, DateTime endDate, string? reason)
    {
        await PostAsync("leave_requests", new
        {
            user_id = userId,
            leave_type = leaveType,
            start_date = startDate.ToString("yyyy-MM-dd"),
            end_date = endDate.ToString("yyyy-MM-dd"),
            reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            status = "Pending",
        });
    }

    /// <summary>Withdraws a request that has not been decided yet.</summary>
    /// <remarks>
    /// Filtered on status as well as id, so a request decided while the phone was showing
    /// the old list is not withdrawn out from under the decision. The row is left in place
    /// rather than deleted: what was asked for is part of the record even when it is taken
    /// back, and the days it was holding are freed by the status alone.
    /// </remarks>
    public async Task CancelLeaveAsync(long requestId)
    {
        // decided_at is stamped here too. Withdrawing settles the request as surely as a
        // decision does, and a history with no time against one of its entries reads as an
        // entry nobody can place.
        //
        // The status filter is what stops a request being withdrawn out from under a
        // decision already made. It also means the write can match nothing, which the
        // server reports as a success, so the rows changed are counted and nought is
        // treated as the refusal it is.
        //
        // AwaitingChange counts as open. It is an approval begun and not finished, so
        // nothing has been granted yet and the request is still the driver's to withdraw.
        // The row policy allows the same two, and a filter narrower than the policy would
        // refuse a withdrawal the database would have accepted.
        var changed = await PatchCountingAsync(
            $"leave_requests?request_id=eq.{requestId}&status=in.(Pending,AwaitingChange)",
            new { status = "Cancelled", decided_at = PhTime.Now });

        if (changed == 0)
            throw new InvalidOperationException(
                "That request could not be cancelled. It may already have been decided.");
    }

    /// <summary>Calls a database function as the signed-in driver.</summary>
    /// <remarks>
    /// For the writes a row policy cannot express. A policy checks the row before the
    /// change and the row after it, and cannot tie the two together, so "may mark this
    /// only if it was already approved" is not a rule it can state. Written as a policy
    /// wide enough to allow the mark, it would also allow a driver to set their own
    /// pending request to approved. The function decides what changes instead, and the
    /// driver is granted nothing beyond the right to call it.
    /// </remarks>
    private static async Task RpcAsync(string function, object args)
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{FleetWiseMobile.SupabaseConfig.Url}/rest/v1/rpc/{function}");
        req.Headers.TryAddWithoutValidation("apikey", FleetWiseMobile.SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FleetWiseMobile.SupabaseConfig.Bearer}");
        req.Content = new StringContent(JsonSerializer.Serialize(args), Encoding.UTF8, "application/json");

        var res = await _http.SendAsync(req);
        await ThrowIfRefusedAsync(res);
    }

    /// <summary>Calls a function that answers with rows, and takes the first.</summary>
    /// <remarks>
    /// A function that returns a table answers as an array, the same shape a table read
    /// does, and an empty array is the answer when the caller may not be told. Null here
    /// therefore means no answer rather than a failure, and every caller treats it that
    /// way.
    /// </remarks>
    private static async Task<T?> RpcReadAsync<T>(string function, object args)
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{FleetWiseMobile.SupabaseConfig.Url}/rest/v1/rpc/{function}");
        req.Headers.TryAddWithoutValidation("apikey", FleetWiseMobile.SupabaseConfig.Key);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FleetWiseMobile.SupabaseConfig.Bearer}");
        req.Content = new StringContent(JsonSerializer.Serialize(args), Encoding.UTF8, "application/json");

        var res = await _http.SendAsync(req);
        await ThrowIfRefusedAsync(res);

        var json = await res.Content.ReadAsStringAsync();
        var list = JsonSerializer.Deserialize<List<T>>(json);
        return list is { Count: > 0 } ? list[0] : default;
    }

    /// <summary>
    /// Asks the dispatcher to cancel leave that has already been granted.
    /// </summary>
    /// <remarks>
    /// An ask, not an act. Handing the days back does not put the driver on a shift: the
    /// week was planned around their absence and only the dispatcher can put them back on
    /// it. A driver who withdrew their own leave would read a rest day on the calendar,
    /// stated with confidence, on days they expected to drive.
    ///
    /// The queue carries the asking, because it is the only thing a dispatcher watches.
    /// </remarks>
    public async Task RequestLeaveWithdrawalAsync(long requestId, string reason)
    {
        await RpcAsync("request_leave_withdrawal", new
        {
            p_request = requestId,
            p_reason = reason,
        });
    }

    /// <summary>Every trip assigned to a driver between two days, whatever its status.</summary>
    /// <remarks>
    /// The calendar draws a month at a time, so it asks for a month at a time rather than
    /// a request per day. Completed and Active trips are included: a driver looking back
    /// over the month wants the days they worked as much as the days they are booked for.
    /// </remarks>
    public async Task<List<Trip>> GetTripsBetweenAsync(int userId, DateTime from, DateTime to)
    {
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("date", Operator.GreaterThanOrEqual, from.ToString("yyyy-MM-dd"))
            .Filter("date", Operator.LessThanOrEqual, to.ToString("yyyy-MM-dd"))
            .Order("date", Ordering.Ascending)
            .Get();
        return r.Models;
    }

    /// <summary>Approved leave overlapping a span of days.</summary>
    /// <remarks>
    /// Overlapping, not contained: a leave that starts in August and ends in September
    /// covers days in both, and asking for requests that begin inside the month would
    /// miss it.
    /// </remarks>
    public async Task<List<LeaveRequest>> GetApprovedLeaveBetweenAsync(int userId, DateTime from, DateTime to)
    {
        var r = await _supabase.From<LeaveRequest>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Filter("status", Operator.Equals, "Approved")
            .Filter("start_date", Operator.LessThanOrEqual, to.ToString("yyyy-MM-dd"))
            .Filter("end_date", Operator.GreaterThanOrEqual, from.ToString("yyyy-MM-dd"))
            .Get();
        return r.Models;
    }

    /// <summary>The weeks marked as built within a span, by Monday, and whether only a roster publish marked each.</summary>
    /// <remarks>
    /// What separates a rest day from a day nobody has scheduled yet. Keyed by week start
    /// rather than by day, because the planner saves a week at a time and every day in a
    /// saved week is answered.
    ///
    /// A week only a roster publish has marked answers just the days of a published month
    /// in it. The publish wrote those days, and not the days of that week that fall in the
    /// month next door.
    /// </remarks>
    public async Task<Dictionary<DateTime, bool>> GetScheduledWeeksAsync(DateTime from, DateTime to)
    {
        var r = await _supabase.From<ScheduleWeek>()
            .Filter("week_start", Operator.GreaterThanOrEqual, from.ToString("yyyy-MM-dd"))
            .Filter("week_start", Operator.LessThanOrEqual, to.ToString("yyyy-MM-dd"))
            .Get();
        return r.Models.GroupBy(w => w.WeekStart.Date).ToDictionary(g => g.Key, g => g.All(w => w.RosterOnly));
    }

    /// <summary>The first days of the months with a published roster, within a span.</summary>
    /// <remarks>
    /// Empty when the roster tables do not exist yet, so the calendar falls back to the
    /// planner's weeks alone rather than failing.
    /// </remarks>
    public async Task<HashSet<DateTime>> GetPublishedMonthsAsync(DateTime from, DateTime to)
    {
        try
        {
            var r = await _supabase.From<RosterMonth>()
                .Filter("month", Operator.GreaterThanOrEqual, FirstOfMonth(from).ToString("yyyy-MM-dd"))
                .Filter("month", Operator.LessThanOrEqual, FirstOfMonth(to).ToString("yyyy-MM-dd"))
                .Filter("status", Operator.Equals, "Published")
                .Get();
            return r.Models.Select(m => m.Month.Date).ToHashSet();
        }
        catch (Postgrest.Exceptions.PostgrestException)
        {
            return new();
        }
    }

    /// <summary>The driver's own roster places in the published months of a span.</summary>
    /// <remarks>
    /// At most one a month. Empty for a driver with no place on the roster, for months not
    /// yet published, and when the roster tables do not exist yet.
    /// </remarks>
    public async Task<List<RosterSlot>> GetRosterSlotsAsync(int userId, DateTime from, DateTime to)
    {
        try
        {
            var r = await _supabase.From<RosterSlot>()
                .Filter("driver_id", Operator.Equals, userId.ToString())
                .Filter("month", Operator.GreaterThanOrEqual, FirstOfMonth(from).ToString("yyyy-MM-dd"))
                .Filter("month", Operator.LessThanOrEqual, FirstOfMonth(to).ToString("yyyy-MM-dd"))
                .Get();
            return r.Models;
        }
        catch (Postgrest.Exceptions.PostgrestException)
        {
            return new();
        }
    }

    private static DateTime FirstOfMonth(DateTime d) => new(d.Year, d.Month, 1);

    /// <summary>Approved leave covering a given day, or null when there is none.</summary>
    /// <remarks>
    /// Asked of the server rather than filtered from the full list, because the home page
    /// wants one answer about one day and not a driver's whole year of requests.
    /// </remarks>
    public async Task<LeaveRequest?> GetApprovedLeaveOnAsync(int userId, DateTime day)
    {
        var iso = day.ToString("yyyy-MM-dd");
        var r = await _supabase.From<LeaveRequest>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Filter("status", Operator.Equals, "Approved")
            .Filter("start_date", Operator.LessThanOrEqual, iso)
            .Filter("end_date", Operator.GreaterThanOrEqual, iso)
            .Get();
        return r.Models.FirstOrDefault(l => LeaveEntitlement.CoversDay(l, day));
    }
}
