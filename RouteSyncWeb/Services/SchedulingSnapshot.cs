using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services
{
    // Everything the scheduling rules read, gathered in one pass so that the rules
    // themselves never touch the database.
    //
    // Kept apart from SchedulingRules so the rules can be exercised with
    // hand-built data, and so every screen that ranks replacements answers from the same
    // kind of reading rather than each assembling its own.
    //
    // A record, so a schedule that does not exist yet (leave about to be granted, covers
    // about to be saved) is a copy with that change made rather than a write.
    public sealed record SchedulingSnapshot
    {
        // Trips from at least 30 days before the dates being ranked to 6 days after.
        //
        // The 30 days behind are what route familiarity is counted over. The week either
        // side is what a run of consecutive working days is counted over.
        public required IReadOnlyList<Trip> Trips { get; init; }

        // Every driver account, whatever its status.
        public required IReadOnlyList<UserModel> Drivers { get; init; }

        public required IReadOnlyList<Vehicle> Vehicles { get; init; }

        // Approved leave overlapping the dates being ranked.
        public required IReadOnlyList<LeaveRequest> Leave { get; init; }

        // Drivers whose availability flag currently reads Unavailable.
        public required IReadOnlySet<int> ReportedUnavailable { get; init; }

        // The most recent unresolved maintenance incident per bus.
        public required IReadOnlyDictionary<string, MaintenanceLog> OpenIncidents { get; init; }

        public required IReadOnlyDictionary<int, string> RouteNames { get; init; }

        // Philippine wall-clock time the snapshot is judged against.
        public required DateTime Now { get; init; }

        // The service day Now falls in, 06:00 to 05:59.
        public DateTime OperationalDay =>
            Now.TimeOfDay < PhClock.DayStartTime ? Now.Date.AddDays(-1) : Now.Date;
    }

    // Reads a SchedulingSnapshot from the database.
    public class SchedulingData
    {
        // How far back route familiarity looks.
        public const int HistoryDays = 30;

        // How far either side a run of consecutive working days is followed.
        public const int RunDays = 6;

        private readonly Supabase.Client _supabase;

        public SchedulingData(Supabase.Client supabase) => _supabase = supabase;

        // A snapshot able to rank any trip dated from first to last.
        public async Task<SchedulingSnapshot> LoadAsync(DateTime first, DateTime last)
        {
            var from = first.Date.AddDays(-HistoryDays).ToString("yyyy-MM-dd");
            var to = last.Date.AddDays(RunDays).ToString("yyyy-MM-dd");

            var tripsTask = _supabase.From<Trip>()
                .Filter("date", Operator.GreaterThanOrEqual, from)
                .Filter("date", Operator.LessThanOrEqual, to)
                .Get();
            var driversTask = _supabase.From<UserModel>()
                .Filter("role_id", Operator.Equals, "2")
                .Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var leaveTask = _supabase.From<LeaveRequest>()
                .Filter("status", Operator.Equals, "Approved")
                .Filter("start_date", Operator.LessThanOrEqual, last.Date.ToString("yyyy-MM-dd"))
                .Filter("end_date", Operator.GreaterThanOrEqual, first.Date.ToString("yyyy-MM-dd"))
                .Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();
            var incidentsTask = _supabase.From<MaintenanceLog>()
                .Filter<object>("resolved_at", Operator.Is, null)
                .Get();
            var routesTask = _supabase.From<BusRoute>().Get();

            await Task.WhenAll(tripsTask, driversTask, vehiclesTask, leaveTask,
                               availabilityTask, incidentsTask, routesTask);

            return new SchedulingSnapshot
            {
                Trips = tripsTask.Result.Models,
                Drivers = driversTask.Result.Models,
                Vehicles = vehiclesTask.Result.Models,
                Leave = leaveTask.Result.Models,
                ReportedUnavailable = availabilityTask.Result.Models
                    .Where(a => string.Equals(a.AvailabilityStatus, "Unavailable", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.UserId)
                    .ToHashSet(),
                OpenIncidents = incidentsTask.Result.Models
                    .Where(l => l.VehicleId != null)
                    .GroupBy(l => l.VehicleId)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.CreatedAt).First()),
                RouteNames = routesTask.Result.Models.ToDictionary(r => r.RouteId, r => r.RouteName),
                Now = PhClock.Now,
            };
        }
    }
}
