using System.Globalization;
using System.Text.Json;
using FleetWise.Models;
using static Postgrest.Constants;

namespace FleetWise.Services
{
    public enum RosterStep
    {
        Done,

        /// <summary>The roster was saved after the version the caller worked from.</summary>
        Stale,

        /// <summary>Refused for a reason a person has to fix, listed in Problems.</summary>
        Refused,

        /// <summary>The schedule moved while publishing; nothing was written and it can be tried again.</summary>
        Clash,
    }

    /// <param name="Lines">For a preview or a publish, what it would do or did, in sentences.</param>
    public sealed record RosterStepResult(
        RosterStep Step,
        int Version = 0,
        IReadOnlyList<string>? Lines = null,
        IReadOnlyList<string>? Problems = null,
        string? Message = null,
        IReadOnlyList<string>? Unmarked = null);

    /// <summary>
    /// Saving, generating and publishing a month's roster, for the Roster page and for the
    /// service that runs the monthly cycle when nobody does.
    /// </summary>
    /// <remarks>
    /// One path whoever asks. A publish from the page and one made on schedule by the server
    /// are worked out, checked, written, reported and announced the same way. Only the
    /// author differs: a person's id, or none, which the audit trail shows as the server.
    /// </remarks>
    public class RosterPublisher
    {
        private const int DriverRoleId = 2;
        private static readonly CultureInfo En = CultureInfo.InvariantCulture;

        private readonly Supabase.Client _supabase;
        private readonly AuditLog _audit;

        public RosterPublisher(Supabase.Client supabase, AuditLog audit)
        {
            _supabase = supabase;
            _audit = audit;
        }

        // ---- Reading ------------------------------------------------------------------------

        public async Task<RosterMonth?> ReadMonthAsync(DateTime month) =>
            (await _supabase.From<RosterMonth>()
                .Filter("month", Operator.Equals, month.ToString("yyyy-MM-dd"))
                .Get()).Models.FirstOrDefault();

        public async Task<List<RosterSlot>> ReadSlotsAsync(DateTime month) =>
            (await _supabase.From<RosterSlot>()
                .Filter("month", Operator.Equals, month.ToString("yyyy-MM-dd"))
                .Get()).Models;

        public async Task<(Dictionary<int, string> Routes, List<Vehicle> Vehicles, List<UserModel> Drivers)> ReadFleetAsync()
        {
            var routesTask = _supabase.From<BusRoute>().Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var driversTask = _supabase.From<UserModel>().Filter("role_id", Operator.Equals, DriverRoleId.ToString()).Get();

            await Task.WhenAll(routesTask, vehiclesTask, driversTask);

            return (routesTask.Result.Models.ToDictionary(r => r.RouteId, r => r.RouteName),
                    vehiclesTask.Result.Models,
                    driversTask.Result.Models);
        }

        public static RosterSeat ToSeat(RosterSlot s) =>
            new(s.DriverId, s.Kind, s.RouteId, s.VehicleId, s.Shift, s.RestWeekday,
                string.IsNullOrWhiteSpace(s.Suggested) ? null : s.Suggested);

        /// <summary>Who drove which bus and route over the last <see cref="SchedulingData.HistoryDays"/> days.</summary>
        public async Task<RosterHistory> ReadHistoryAsync()
        {
            var today = PhClock.OperationalDay;
            var trips = await PagedRead.AllAsync(() => _supabase.From<Trip>()
                .Filter("date", Operator.GreaterThanOrEqual, today.AddDays(-SchedulingData.HistoryDays).ToString("yyyy-MM-dd"))
                .Filter("date", Operator.LessThanOrEqual, today.ToString("yyyy-MM-dd"))
                .Order("trip_id", Ordering.Ascending));
            return new RosterHistory(trips);
        }

        /// <summary>A roster cleared of drivers and buses that can no longer be on it, and its empty places filled by rule.</summary>
        /// <remarks>Nothing is saved. See <see cref="RosterRules.AutoFill"/>.</remarks>
        public async Task<AutoFillResult> AutoFillAsync(IReadOnlyList<RosterSeat> seats)
        {
            var fleetTask = ReadFleetAsync();
            var historyTask = ReadHistoryAsync();
            await Task.WhenAll(fleetTask, historyTask);

            var (routes, vehicles, drivers) = fleetTask.Result;
            return RosterRules.AutoFill(seats, vehicles, drivers, routes, historyTask.Result);
        }

        /// <summary>Gaps still to come that nothing has filled: no trip on the slot and no skip.</summary>
        public async Task<List<RosterGap>> OpenGapsAsync(
            IReadOnlyList<RosterGap> gaps, IReadOnlyList<RosterSkip> skips, DateTime today)
        {
            var ahead = gaps.Where(g => g.Date.Date > today).ToList();
            if (ahead.Count == 0) return ahead;

            var filled = new HashSet<(DateTime, string, string)>();
            var trips = await PagedRead.AllAsync(() => _supabase.From<Trip>()
                .Filter("date", Operator.In, ahead.Select(g => (object)g.Date.ToString("yyyy-MM-dd")).Distinct().ToList())
                .Order("trip_id", Ordering.Ascending));
            foreach (var t in trips) filled.Add((t.Date.Date, t.VehicleId.ToUpperInvariant(), t.ShiftType));
            foreach (var s in skips) filled.Add((s.Date.Date, s.VehicleId.ToUpperInvariant(), s.Shift));

            return ahead.Where(g => !filled.Contains((g.Date.Date, g.VehicleId.ToUpperInvariant(), g.Shift))).ToList();
        }

        // ---- Saving and generating ---------------------------------------------------------------

        /// <summary>Saves a month's seats whole through save_roster_month.</summary>
        public async Task<RosterStepResult> SaveAsync(
            DateTime month, int baseVersion, IReadOnlyList<RosterSeat> seats, int? savedBy, string auditAction)
        {
            try
            {
                var response = await _supabase.Rpc("save_roster_month", new Dictionary<string, object?>
                {
                    ["p_month"] = month.ToString("yyyy-MM-dd"),
                    ["p_base_version"] = baseVersion,
                    ["p_slots"] = seats.Select(s => new Dictionary<string, object?>
                    {
                        ["driver_id"] = s.DriverId,
                        ["kind"] = s.Kind,
                        ["route_id"] = s.RouteId,
                        ["vehicle_id"] = s.VehicleId,
                        ["shift"] = s.Shift,
                        ["rest_weekday"] = s.RestWeekday,
                        ["suggested"] = s.Suggested,
                    }).ToList(),
                    ["p_saved_by"] = savedBy,
                });

                return new(RosterStep.Done, int.Parse(response.Content?.Trim() ?? "", En));
            }
            catch (Postgrest.Exceptions.PostgrestException ex) when (DatabaseCode(ex.Content) is "RS409")
            {
                return new(RosterStep.Stale, Message: StaleMessage(month));
            }
            catch (Postgrest.Exceptions.PostgrestException ex)
            {
                var why = DatabaseMessage(ex.Content) ?? ex.Message;
                await _audit.WriteAsync(auditAction,
                    $"could not save the {month:MMMM yyyy} roster: {why}",
                    "roster_months", month.ToString("yyyy-MM-dd"), outcome: "failed");

                return new(RosterStep.Refused, Problems: new[] { "The roster could not be saved: " + (DatabaseMessage(ex.Content) ?? "the database refused it.") });
            }
        }

        /// <summary>
        /// Fills a month's draft with the month before's roster, every shift rotated one step
        /// backward, then auto-filled.
        /// </summary>
        /// <remarks>
        /// Auto-filled so a driver deactivated or a bus retired since last month is dealt with in
        /// the draft, with what changed marked for review, rather than stopping the publish on
        /// the 25th. Lines says what auto-fill did.
        /// </remarks>
        /// <param name="by">The person generating it, or null when the monthly cycle does.</param>
        public async Task<RosterStepResult> GenerateAsync(DateTime month, int baseVersion, int? by)
        {
            var roster = await ReadMonthAsync(month);
            if (roster?.Status == "Published")
                return Refused($"The {month:MMMM} roster is already published. Change it on the Roster page and publish the changes instead.");

            var previous = month.AddMonths(-1);
            var slots = await ReadSlotsAsync(previous);
            if (slots.Count == 0)
                return Refused($"{previous:MMMM} has no roster to carry forward.");

            var rotated = RosterGenerator.Rotate(slots.Select(ToSeat).ToList());
            var fill = await AutoFillAsync(rotated);
            var seats = fill.Seats;

            var saved = await SaveAsync(month, baseVersion, seats, by, "roster_generated");
            if (saved.Step != RosterStep.Done) return saved;

            await _supabase.From<RosterMonth>()
                .Filter("month", Operator.Equals, month.ToString("yyyy-MM-dd"))
                .Set(m => m.GeneratedAt!, DateTime.UtcNow)
                .Set(m => m.GeneratedBy!, by?.ToString(En) ?? "system")
                .Update();

            await _audit.WriteAsync("roster_generated",
                (by is null ? "drafted" : "generated")
                    + $" the {month:MMMM yyyy} roster from {previous:MMMM}'s, every shift rotated: "
                    + $"{seats.Count(s => s.Kind == RosterRules.Crew)} crew seats, "
                    + $"{seats.Count(s => s.Kind == RosterRules.Floater)} floaters"
                    + (fill.Changed ? "; auto-fill " + AutoFillSummary(fill) : ""),
                "roster_months", month.ToString("yyyy-MM-dd"));

            // Said only when auto-fill had something to say: a change, or a place nobody was free for.
            return saved with
            {
                Lines = fill.Changed || fill.LeftEmpty > 0 ? fill.Notes : Array.Empty<string>(),
                Unmarked = fill.Unmarked,
            };
        }

        /// <summary>What auto-fill did, counted, for an audit line or a badge.</summary>
        public static string AutoFillSummary(AutoFillResult fill)
        {
            var parts = new List<string>();
            if (fill.Filled > 0) parts.Add(fill.Filled == 1 ? "filled 1 place" : $"filled {fill.Filled} places");
            if (fill.Emptied > 0) parts.Add(fill.Emptied == 1 ? "emptied 1 place" : $"emptied {fill.Emptied} places");
            if (fill.RestDaysSet > 0) parts.Add(fill.RestDaysSet == 1 ? "set 1 rest day" : $"set {fill.RestDaysSet} rest days");
            if (fill.LeftEmpty > 0) parts.Add(fill.LeftEmpty == 1 ? "left 1 place empty" : $"left {fill.LeftEmpty} places empty");
            return parts.Count == 0 ? "changed nothing" : string.Join(", ", parts);
        }

        // ---- Publishing -----------------------------------------------------------------------------

        /// <summary>What publishing the saved roster would write, without writing it.</summary>
        public async Task<RosterStepResult> PreviewAsync(DateTime month, int version)
        {
            var built = await BuildPlanAsync(month, version);
            if (built.Refusal is not null) return built.Refusal;

            return new(RosterStep.Done, version, ReportLines(month, built.Plan!, built.Names!, built.Seats!, result: null));
        }

        /// <summary>Publishes the saved roster as the month's trips, and tells the drivers.</summary>
        /// <param name="version">The version the caller worked from, or null to take the roster as it stands.</param>
        /// <param name="by">The person publishing, or null when the monthly cycle does.</param>
        public async Task<RosterStepResult> PublishAsync(DateTime month, int? version, int? by)
        {
            var built = await BuildPlanAsync(month, version);
            if (built.Refusal is not null) return built.Refusal;

            var plan = built.Plan!;
            var baseVersion = built.Roster!.Version;
            var wasPublished = built.Roster.Status == "Published";
            PublishResult result;

            try
            {
                var response = await _supabase.Rpc("publish_roster_month", new Dictionary<string, object?>
                {
                    ["p_month"] = month.ToString("yyyy-MM-dd"),
                    ["p_base_version"] = baseVersion,
                    ["p_plan"] = PlanJson(plan),
                    ["p_published_by"] = by,
                });

                result = JsonSerializer.Deserialize<PublishResult>(response.Content ?? "{}",
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PublishResult();
            }
            catch (Postgrest.Exceptions.PostgrestException ex) when (DatabaseCode(ex.Content) is "RS409")
            {
                return new(RosterStep.Stale, Message: StaleMessage(month));
            }
            catch (Postgrest.Exceptions.PostgrestException ex) when (DatabaseCode(ex.Content) is "RS422")
            {
                return new(RosterStep.Clash, Message:
                    "The schedule changed while the roster was being published, so nothing was written. "
                    + "Publish again to work from the schedule as it is now.");
            }
            catch (Postgrest.Exceptions.PostgrestException ex)
            {
                await _audit.WriteAsync("roster_published",
                    $"could not publish the {month:MMMM yyyy} roster: {DatabaseMessage(ex.Content) ?? ex.Message}",
                    "roster_months", month.ToString("yyyy-MM-dd"), outcome: "failed");

                return Refused("The roster could not be published: " + (DatabaseMessage(ex.Content) ?? "the database refused it."));
            }

            var lines = ReportLines(month, plan, built.Names!, built.Seats!, result);

            await _audit.WriteAsync("roster_published",
                (by is null ? "published on schedule" : "published")
                    + $" the {month:MMMM yyyy} roster: {result.Inserted} trips written, {result.Updated} changed, "
                    + $"{result.Deleted} removed, {plan.Gaps.Count} slots unfilled",
                "roster_months", month.ToString("yyyy-MM-dd"));

            // Best effort, and after the trips are written. The roster stands whether or not
            // every driver has been told.
            try
            {
                if (wasPublished)
                    await TellChangedDriversAsync(plan, built.Trips!, by ?? 0);
                else
                    await TellEveryDriverAsync(month, built.Seats!, built.Routes!, by ?? 0);
            }
            catch (Exception ex)
            {
                await _audit.WriteAsync("roster_notice_failed",
                    $"published the {month:MMMM yyyy} roster but could not tell the drivers: {ex.Message}",
                    "roster_months", month.ToString("yyyy-MM-dd"), outcome: "failed");
            }

            return new(RosterStep.Done, result.Version, lines);
        }

        private sealed class PublishResult
        {
            public int Version { get; set; }
            public int Inserted { get; set; }
            public int Updated { get; set; }
            public int Deleted { get; set; }
            public int Dropped { get; set; }
            public List<string> Kept { get; set; } = new();
        }

        private sealed record Built(
            RosterMonth? Roster, PublishPlan? Plan, Dictionary<int, string>? Names,
            IReadOnlyList<RosterSeat>? Seats, IReadOnlyDictionary<int, string>? Routes,
            IReadOnlyList<Trip>? Trips, RosterStepResult? Refusal);

        private static RosterStepResult Refused(string problem) => new(RosterStep.Refused, Problems: new[] { problem });

        /// <summary>The publish plan for the month as saved, or why there cannot be one.</summary>
        private async Task<Built> BuildPlanAsync(DateTime month, int? version)
        {
            Built No(RosterStepResult r) => new(null, null, null, null, null, null, r);

            if (month < FirstOf(PhClock.OperationalDay))
                return No(Refused("That month is over, so its roster can no longer be published."));

            var roster = await ReadMonthAsync(month);
            if (roster is null)
                return No(Refused($"Save the {month:MMMM} roster before publishing it."));
            if (version is int v && roster.Version != v)
                return No(new RosterStepResult(RosterStep.Stale, Message: StaleMessage(month)));

            var slots = await ReadSlotsAsync(month);
            if (slots.Count == 0)
                return No(Refused($"The {month:MMMM} roster has nobody on it yet."));

            var seats = slots.Select(ToSeat).ToList();
            var (routes, vehicles, drivers) = await ReadFleetAsync();

            var problems = RosterRules.Problems(seats, vehicles, drivers, routes);
            if (problems.Count > 0)
                return No(new RosterStepResult(RosterStep.Refused, Problems: problems));

            var from = month.AddDays(-7).ToString("yyyy-MM-dd");
            var to = month.AddMonths(1).AddDays(6).ToString("yyyy-MM-dd");

            var tripsTask = PagedRead.AllAsync(() => _supabase.From<Trip>()
                .Filter("date", Operator.GreaterThanOrEqual, from)
                .Filter("date", Operator.LessThanOrEqual, to)
                .Order("trip_id", Ordering.Ascending));
            var marksTask = PagedRead.AllAsync(() => _supabase.From<TripRosterState>()
                .Select("trip_id,roster_month,hand_edited,date")
                .Filter("date", Operator.GreaterThanOrEqual, from)
                .Filter("date", Operator.LessThanOrEqual, to)
                .Order("trip_id", Ordering.Ascending));
            var skipsTask = PagedRead.AllAsync(() => _supabase.From<RosterSkip>()
                .Filter("month", Operator.Equals, month.ToString("yyyy-MM-dd"))
                .Order("date", Ordering.Ascending)
                .Order("vehicle_id", Ordering.Ascending)
                .Order("shift", Ordering.Ascending));
            var leaveTask = _supabase.From<LeaveRequest>()
                .Filter("status", Operator.Equals, "Approved")
                .Filter("start_date", Operator.LessThanOrEqual, to)
                .Filter("end_date", Operator.GreaterThanOrEqual, from)
                .Get();

            await Task.WhenAll(tripsTask, marksTask, skipsTask, leaveTask);

            var world = new RosterWorld
            {
                Month = month,
                OperationalDay = PhClock.OperationalDay,
                Now = PhClock.Now,
                Seats = seats,
                Trips = tripsTask.Result,
                Marks = marksTask.Result.ToDictionary(m => m.TripId, m => new TripRosterMark(m.RosterMonth, m.HandEdited)),
                Drivers = drivers,
                Vehicles = vehicles,
                Leave = leaveTask.Result.Models,
                Skips = skipsTask.Result.Select(s => (s.Date.Date, s.VehicleId, s.Shift)).ToHashSet(),
                RouteNames = routes,
            };

            var names = drivers.ToDictionary(d => d.UserId, d =>
            {
                var n = $"{d.FirstName} {d.LastName}".Trim();
                return n.Length == 0 ? $"Driver {d.UserId}" : n;
            });

            return new(roster, RosterGenerator.Plan(world), names, seats, routes, tripsTask.Result, null);
        }

        /// <summary>The plan as publish_roster_month reads it.</summary>
        private static Dictionary<string, object?> PlanJson(PublishPlan plan) => new()
        {
            ["inserts"] = plan.Inserts.Select(i => new Dictionary<string, object?>
            {
                ["date"] = i.Date.ToString("yyyy-MM-dd"),
                ["shift"] = i.Shift,
                ["route_id"] = i.RouteId,
                ["vehicle_id"] = i.VehicleId,
                ["driver_id"] = i.DriverId,
                ["shift_start_time"] = i.Start.ToString(@"hh\:mm"),
                ["shift_end_time"] = i.End.ToString(@"hh\:mm"),
                ["break_start"] = i.BreakStart.ToString(@"hh\:mm"),
            }).ToList(),
            ["updates"] = plan.Updates.Select(u => new Dictionary<string, object?>
            {
                ["trip_id"] = u.TripId,
                ["driver_id"] = u.DriverId,
                ["break_start"] = u.BreakStart.ToString(@"hh\:mm"),
            }).ToList(),
            ["deletes"] = plan.Deletes.ToList(),
            ["gaps"] = plan.Gaps.Select(g => new Dictionary<string, object?>
            {
                ["date"] = g.Date.ToString("yyyy-MM-dd"),
                ["vehicle_id"] = g.VehicleId,
                ["shift"] = g.Shift,
                ["route_id"] = g.RouteId,
                ["reason"] = g.Reason,
            }).ToList(),
        };

        /// <summary>What a publish would do, or did, in plain sentences.</summary>
        /// <param name="result">Null for a preview, which is written in the future tense.</param>
        private static List<string> ReportLines(
            DateTime month, PublishPlan plan, IReadOnlyDictionary<int, string> names,
            IReadOnlyList<RosterSeat> seats, PublishResult? result)
        {
            var done = result is not null;
            var lines = new List<string>();
            string N(int n) => n.ToString("N0", En);
            string Trips(int n) => n == 1 ? "1 trip" : $"{N(n)} trips";

            if (plan.From > plan.To)
            {
                lines.Add($"Every day of {month:MMMM} has already begun, so there is nothing left to write.");
                return lines;
            }

            var span = plan.From == month
                ? $"for {month:MMMM}"
                : $"from {plan.From.ToString("MMMM d", En)} to the end of {month:MMMM}";

            var written = done ? result!.Inserted : plan.Inserts.Count;
            var changed = done ? result!.Updated : plan.Updates.Count;
            var removed = done ? result!.Deleted : plan.Deletes.Count;

            lines.Add(written == 0
                ? (done ? $"Published {month:MMMM}. No new trips to write." : "No new trips to write.")
                : (done ? $"Published {month:MMMM}. Wrote {Trips(written)} {span}." : $"Writes {Trips(written)} {span}."));
            if (changed > 0) lines.Add(done ? $"Changed the driver or break on {Trips(changed)}." : $"Changes the driver or break on {Trips(changed)}.");
            if (removed > 0) lines.Add(done ? $"Removed {Trips(removed)} the roster no longer runs or cannot fill." : $"Removes {Trips(removed)} the roster no longer runs or cannot fill.");
            if (plan.Unchanged > 0) lines.Add($"{Trips(plan.Unchanged)} already match the roster.");

            var built = plan.Kept.Count(k => !k.ByHand);
            var edited = plan.Kept.Count(k => k.ByHand);
            if (built > 0) lines.Add(done ? $"Kept {Trips(built)} made by hand." : $"Builds around {Trips(built)} made by hand.");
            if (edited > 0) lines.Add(done ? $"Kept {Trips(edited)} edited by hand." : $"Keeps {Trips(edited)} edited by hand.");

            if (plan.CoversFilled > 0)
                lines.Add((done ? "Filled " : "Fills ") + (plan.CoversFilled == 1 ? "1 rest day or absence" : $"{N(plan.CoversFilled)} rest days and absences") + " from floaters.");

            var nobody = seats.Count(s => s.Kind == RosterRules.Crew && s.DriverId is null);
            if (nobody > 0)
                lines.Add((nobody == 1 ? "1 bus shift has" : $"{N(nobody)} bus shifts have")
                    + " nobody rostered on it, so floaters cover " + (nobody == 1 ? "it" : "them") + " where they can.");

            if (plan.Gaps.Count > 0)
                lines.Add(plan.Gaps.Count == 1 ? "1 slot is left unfilled." : $"{N(plan.Gaps.Count)} slots are left unfilled.");

            if (plan.Skipped > 0)
                lines.Add(plan.Skipped == 1 ? "1 skipped slot stays empty." : $"{N(plan.Skipped)} skipped slots stay empty.");

            if (plan.Idle.Count > 0)
            {
                var who = plan.Idle.Select(i => names.GetValueOrDefault(i.DriverId, $"Driver {i.DriverId}")).Distinct().ToList();
                var shown = string.Join(", ", who.Take(3)) + (who.Count > 3 ? $" and {who.Count - 3} more" : "");
                lines.Add((plan.Idle.Count == 1 ? "1 shift leaves a crew driver idle" : $"{N(plan.Idle.Count)} shifts leave a crew driver idle")
                    + $", because a trip made by hand took their bus: {shown}.");
            }

            foreach (var (bus, count) in plan.OutOfServiceUse.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                lines.Add($"Bus {bus} is out of service now; {Trips(count)} this month use it.");

            if (done && (result!.Kept.Count > 0 || result.Dropped > 0))
                lines.Add($"{Trips(result.Kept.Count + result.Dropped)} changed while publishing and were left as they were.");

            return lines;
        }

        // ---- Telling drivers --------------------------------------------------------------------------

        /// <summary>One message to each driver on a newly published roster, saying what their month is.</summary>
        /// <remarks>
        /// Written to be understood at a glance: the bus, the shift and its hours, the rest day
        /// and the break for a crew driver; the route, the usual shift and the rest day for a
        /// floater, whose days are in their calendar.
        /// </remarks>
        private async Task TellEveryDriverAsync(
            DateTime month, IReadOnlyList<RosterSeat> seats, IReadOnlyDictionary<int, string> routes, int sender)
        {
            // The same order the publish staggers breaks by: every bus the route runs on the
            // shift, whether or not anybody is rostered on it.
            var breakIndex = seats.Where(s => s.Kind == RosterRules.Crew)
                .GroupBy(s => (s.RouteId, Shift: s.Shift.ToUpperInvariant()))
                .SelectMany(g => g.OrderBy(s => s.VehicleId, StringComparer.Ordinal).Select((s, i) => (s, i)))
                .ToDictionary(x => (Bus: x.s.VehicleId!.ToUpperInvariant(), Shift: x.s.Shift.ToUpperInvariant()), x => x.i);

            var messages = seats.Where(s => s.DriverId is not null).Select(s =>
            {
                var rest = RosterRules.DayName(s.RestWeekday ?? 0);
                string body;

                if (s.Kind == RosterRules.Crew && TripStatus.Windows.TryGetValue(s.Shift, out var window))
                {
                    var slots = BreakSlots.For(window.Start);
                    var breakStart = slots[breakIndex.GetValueOrDefault((s.VehicleId!.ToUpperInvariant(), s.Shift.ToUpperInvariant())) % slots.Count];
                    body = $"You are assigned to Bus {s.VehicleId} on the {s.Shift} shift, "
                         + $"{Clock(window.Start)} to {Clock(window.End)}. Your rest day is {rest} and your break is "
                         + $"from {BreakSlots.Label(breakStart)}. You can view each day in your schedule calendar.";
                }
                else
                {
                    var route = routes.GetValueOrDefault(s.RouteId, $"route {s.RouteId}");
                    body = $"You are a floater on {route} this month, covering rest days mostly on the {s.Shift} shift. "
                         + $"Your rest day is {rest}. Please check your schedule calendar for your daily assignments.";
                }

                return new Message
                {
                    SenderId = sender,
                    TargetAudience = "Driver",
                    TargetId = s.DriverId!.Value.ToString(En),
                    Subject = $"Your {month:MMMM} Schedule",
                    Body = body,
                    Priority = "Normal",
                    CreatedAt = PhClock.NowForDb,
                };
            }).ToList();

            if (messages.Count > 0) await _supabase.From<Message>().Insert(messages);
        }

        /// <summary>
        /// On a re-publish, one notice to each driver whose own days changed, naming those days.
        /// </summary>
        /// <remarks>
        /// The same notice a planner save sends, so a change reaches a driver in the same words
        /// whichever screen made it. A driver taken off a trip and the one put on it both hear.
        /// </remarks>
        private async Task TellChangedDriversAsync(PublishPlan plan, IReadOnlyList<Trip> trips, int sender)
        {
            var byId = trips.ToDictionary(t => t.TripId);
            var moved = new Dictionary<int, SortedSet<DateTime>>();

            void Add(int driverId, DateTime day)
            {
                if (driverId == 0) return;
                if (!moved.TryGetValue(driverId, out var days)) moved[driverId] = days = new SortedSet<DateTime>();
                days.Add(day.Date);
            }

            foreach (var i in plan.Inserts) Add(i.DriverId, i.Date);
            foreach (var u in plan.Updates)
            {
                Add(u.DriverId, u.Date);
                if (byId.TryGetValue(u.TripId, out var was) && was.DriverId != u.DriverId) Add(was.DriverId, u.Date);
            }
            foreach (var d in plan.Deletes)
                if (byId.TryGetValue(d, out var gone)) Add(gone.DriverId, gone.Date);

            var soon = PhClock.OperationalDay.AddDays(1);
            var messages = moved.Select(kv => new Message
            {
                SenderId = sender,
                TargetAudience = "Driver",
                TargetId = kv.Key.ToString(En),
                Subject = "Schedule updated",
                Body = $"Your schedule has changed for {DayList(kv.Value.ToList())}. Open your calendar to review the details.",
                Priority = kv.Value.Any(d => d <= soon) ? "High" : "Normal",
                CreatedAt = PhClock.NowForDb,
            }).ToList();

            if (messages.Count > 0) await _supabase.From<Message>().Insert(messages);
        }

        /// <summary>Days as they would be read aloud, or as a count and a span once there are too many to read.</summary>
        private static string DayList(List<DateTime> days)
        {
            if (days.Count > 6)
                return $"{days.Count} days between {days[0].ToString("MMM d", En)} and {days[^1].ToString("MMM d", En)}";

            var written = days.Select(d => d.ToString("MMM d", En)).ToList();
            if (written.Count == 1) return written[0];
            if (written.Count == 2) return $"{written[0]} and {written[1]}";
            return string.Join(", ", written.Take(written.Count - 1)) + $" and {written[^1]}";
        }

        private static string Clock(TimeSpan t) => BreakSlots.Clock(t);

        // ---- Small things ----------------------------------------------------------------------------------

        public static DateTime FirstOf(DateTime day) => new(day.Year, day.Month, 1);

        public static string StaleMessage(DateTime month) =>
            $"Someone saved the {month:MMMM} roster after this page was opened. Reload to see their changes first.";

        /// <summary>The SQLSTATE PostgREST reports for a refused call, or null.</summary>
        public static string? DatabaseCode(string? content) => ReadErrorField(content, "code");

        public static string? DatabaseMessage(string? content) => ReadErrorField(content, "message");

        private static string? ReadErrorField(string? content, string field)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            try
            {
                using var doc = JsonDocument.Parse(content);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                       && doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
