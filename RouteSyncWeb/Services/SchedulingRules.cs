using FleetWise.Models;

namespace FleetWise.Services
{
    // A driver who could take a trip, and why they sit where they do.
    // Rank: Position in the ranking, from 1. Zero for a driver listed but never ranked.
    // Tier: 1 is best. See SchedulingRules.RankDrivers.
    // Reason: One line a dispatcher can read without knowing the rules.
    // Warning: What taking this driver costs, or null when it costs nothing.
    // Facts: The figures the driver was ranked on, for showing beside the name.
    public sealed record DriverCandidate(
        int DriverId, string Name, int Rank, int Tier, string Reason, string? Warning, DriverFacts? Facts = null);

    // The figures a driver is ranked on.
    // RouteTrips: Trips on the trip's route in the last SchedulingData.HistoryDays days.
    // RouteName: The trip's route.
    // WeekShifts: Shifts already in the trip's Monday to Sunday week.
    // OtherShift: Another shift the driver has on the trip's day, or null.
    // DaysInRow: How many days in a row taking the trip would make, counting its day.
    public sealed record DriverFacts(int RouteTrips, string RouteName, int WeekShifts, string? OtherShift, int DaysInRow);

    // A bus that could take a trip, and why it sits where it does.
    // Facts: The figures the bus was ranked on, for showing beside it.
    public sealed record VehicleCandidate(
        string VehicleId, string PlateNumber, int Rank, int Tier, string Reason, string? Warning, VehicleFacts? Facts = null);

    // The figures a bus is ranked on.
    // FewerSeatsThan: The bus it would replace, when it has fewer seats than that bus; otherwise null.
    // HomeRoute: The route it is based on, or null when it has none.
    // SameRoute: Whether it is based on the trip's own route.
    // WeekTrips: Trips already in the trip's Monday to Sunday week.
    public sealed record VehicleFacts(int Seats, string? FewerSeatsThan, string? HomeRoute, bool SameRoute, int WeekTrips);

    // Drivers a ranking left out before ranking anyone, counted by why.
    //
    // Kept so a list with nobody good on it can say why rather than only that. The driver
    // already on the trip and accounts that are not active drivers are not counted: neither
    // was ever a candidate.
    public sealed record DriverExclusions(int OnThatShift, int ReportedUnavailable);

    // Drivers ranked for a trip, and those left out of the ranking for being on leave.
    //
    // Leave is kept to one side rather than dropped. A driver on leave who offers to cover
    // is a thing that happens, and the assignment rules let a dispatcher confirm past it,
    // so the name has to stay reachable even though it is never suggested.
    public sealed record DriverRanking(
        IReadOnlyList<DriverCandidate> Candidates, IReadOnlyList<DriverCandidate> OnLeave, DriverExclusions? Excluded = null);

    // Buses a ranking left out before ranking any, counted by why. Retired buses are not counted.
    public sealed record VehicleExclusions(int OnThatShift, int OutOfService);

    // Buses ranked for a trip, and how many were left out.
    public sealed record VehicleRanking(IReadOnlyList<VehicleCandidate> Candidates, VehicleExclusions Excluded);

    // Where a chosen replacement sat in the ranking offered for it.
    // Rank: Its position, or null when it was not on the list.
    // Of: How many candidates were ranked.
    public sealed record RankedPick(int? Rank, int? Tier, int Of);

    // The screens a pick from a ranking is made on, as recorded in the audit trail.
    public static class PickScreen
    {
        public const string Board = "board";
        public const string Reassign = "reassign";
        public const string Cover = "cover";
        public const string Roster = "roster";
        public const string Planner = "planner";

        // Every screen, in the order a report lists them.
        public static readonly IReadOnlyList<string> All = new[] { Board, Reassign, Cover, Roster, Planner };

        public static bool IsKnown(string? screen) => screen is not null && All.Contains(screen);
    }

    // What a reassignment chose, measured against what was suggested.
    // Screen: Where the pick was made, one of PickScreen, or null when not known.
    public sealed record ReassignmentTag(
        bool TookTopSuggestion,
        IReadOnlyList<string> Issues,
        RankedPick? Driver,
        RankedPick? Vehicle,
        string? Screen = null)
    {
        // The value written to audit_log.changes.
        //
        // Nested under its own key so the audit page, which reads old and new
        // as a row edit, never mistakes it for one.
        public Dictionary<string, object?> ToAuditChanges() => new()
        {
            ["recommendation"] = new Dictionary<string, object?>
            {
                ["via"] = TookTopSuggestion ? "suggestion" : "manual",
                ["issues"] = Issues,
                ["driver"] = Driver is null ? null : Pick(Driver),
                ["vehicle"] = Vehicle is null ? null : Pick(Vehicle),
                ["screen"] = Screen,
            },
        };

        private static Dictionary<string, object?> Pick(RankedPick p) => new()
        {
            ["rank"] = p.Rank,
            ["tier"] = p.Tier,
            ["of"] = p.Of,
        };
    }

    // A shift standing in the way of leave, and who could take it over.
    // Candidates: Drivers who could cover it at no cost worth a confirm, best first.
    // Suggested: The one offered by default, or null when nobody qualifies.
    // Shortfall: Why nobody qualifies, when nobody does; see SchedulingRules.DriverShortfall.
    // PinnedDriverId:
    // The driver the dispatcher already chose for this shift, when they did, or
    // SchedulingRules.LeftForNow when they chose to leave it uncovered for now.
    // PinnedProblem: Why that choice can no longer be saved, or null when it can.
    public sealed record CoverShift(
        Trip Trip, IReadOnlyList<DriverCandidate> Candidates, DriverCandidate? Suggested,
        string? Shortfall = null, int? PinnedDriverId = null, string? PinnedProblem = null);

    // The reasons a trip cannot run as it is assigned.
    public static class AssignmentIssue
    {
        public const string VehicleOutOfService = "vehicle_out_of_service";
        public const string DriverUnavailable = "driver_unavailable";
        public const string DriverOnLeave = "driver_on_leave";
    }

    // The published rules for choosing a replacement driver or bus.
    //
    // Every function here is pure: it reads a SchedulingSnapshot and
    // returns an answer, and never writes. The dispatch board, the reassign modal and the
    // audit trail all rank through this class, so a suggestion made on one screen is the
    // suggestion measured on another.
    //
    // Ranking is by tier and then by tie-breakers, never by weighted points. Each position
    // is explained by a rule that can be said out loud, which is the whole case for
    // trusting it. Nothing here applies a suggestion; a dispatcher does.
    //
    // The eligibility tests mirror the assignment rules in TripAssignments, so that a
    // candidate in tiers 1 to 3 saves without a conflict and one in tier 4 raises exactly
    // the conflict its warning names.
    public static class SchedulingRules
    {
        private const int DriverRoleId = 2;

        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

        private static readonly Dictionary<string, string> NextShift = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Morning"] = "Afternoon",
            ["Afternoon"] = "Evening",
        };

        // Why a trip cannot run as assigned, in the vocabulary of AssignmentIssue.
        // Empty when it can.
        //
        // The availability flag carries no date. It speaks for the operational day it is
        // read on and no other, so a driver who called in sick this morning is not an issue
        // on next Thursday's trip. Leave has dates and is read for the trip's own day.
        public static IReadOnlyList<string> IssuesOf(Trip trip, SchedulingSnapshot s)
        {
            var issues = new List<string>();
            var day = trip.Date.Date;

            var vehicle = s.Vehicles.FirstOrDefault(v => Ci.Equals(v.VehicleId, trip.VehicleId));
            if (vehicle?.OutOfService == true)
                issues.Add(AssignmentIssue.VehicleOutOfService);

            var driver = s.Drivers.FirstOrDefault(d => d.UserId == trip.DriverId);
            if (driver is null || !IsActive(driver)
                || (s.ReportedUnavailable.Contains(trip.DriverId) && day == s.OperationalDay))
            {
                issues.Add(AssignmentIssue.DriverUnavailable);
            }
            else if (s.Leave.Any(l => l.UserId == trip.DriverId && LeaveEntitlement.CoversDay(l, day)))
            {
                issues.Add(AssignmentIssue.DriverOnLeave);
            }

            return issues;
        }

        // Whether any of the issues is about the driver rather than the bus.
        public static bool IsDriverIssue(IEnumerable<string> issues) =>
            issues.Any(i => i is AssignmentIssue.DriverUnavailable or AssignmentIssue.DriverOnLeave);

        // Whether any of the issues is about the bus.
        public static bool IsVehicleIssue(IEnumerable<string> issues) =>
            issues.Contains(AssignmentIssue.VehicleOutOfService);

        // Drivers who could take this trip instead of the one on it, best first.
        //
        // Never ranked: the driver already on the trip, an account that is not
        // active, a driver who reported they cannot drive today when the trip is today, and
        // a driver already booked on the same shift that day. A driver on approved leave is
        // listed apart, in DriverRanking.OnLeave.
        //
        // Tiers, best first:
        // 1. No other shift that day.
        // 2. Another shift that day that breaks no rule.
        // 3. Would make seven or more working days in a row.
        // 4. Breaks a back-to-back or an Evening then Morning rule.
        //
        // Inside a tier: most trips on this route in the last 30 days, then fewest
        // shifts this week, then name.
        public static DriverRanking RankDrivers(Trip trip, SchedulingSnapshot s)
        {
            var day = trip.Date.Date;
            var others = s.Trips.Where(t => t.TripId != trip.TripId).ToList();
            var (weekStart, weekEnd) = WeekOf(day);
            var routeName = RouteName(s, trip.RouteId);

            var ranked = new List<(DriverCandidate C, int Familiarity, int Week)>();
            var onLeave = new List<DriverCandidate>();
            int onThatShift = 0, unavailable = 0;

            foreach (var driver in s.Drivers)
            {
                if (driver.UserId == trip.DriverId) continue;
                if (driver.RoleId != DriverRoleId || !IsActive(driver)) continue;
                if (s.ReportedUnavailable.Contains(driver.UserId) && day == s.OperationalDay)
                {
                    unavailable++;
                    continue;
                }

                var mine = others.Where(t => t.DriverId == driver.UserId).ToList();
                if (mine.Any(t => t.Date.Date == day && Ci.Equals(t.ShiftType, trip.ShiftType)))
                {
                    onThatShift++;
                    continue;
                }

                var name = LabelOf(driver, s);

                var leave = s.Leave.FirstOrDefault(l =>
                    l.UserId == driver.UserId && LeaveEntitlement.CoversDay(l, day));
                if (leave is not null)
                {
                    onLeave.Add(new DriverCandidate(driver.UserId, name, 0, 0,
                        $"On approved {leave.LeaveType?.ToLowerInvariant()} leave", null));
                    continue;
                }

                var broken = RestRuleBroken(trip.ShiftType, day, mine);
                var run = ConsecutiveRun(day, mine, s.Now);
                var sameDay = mine.Where(t => t.Date.Date == day).OrderBy(t => t.ShiftStartTime).ToList();

                var tier = broken is not null ? 4
                         : run > SchedulingData.RunDays ? 3
                         : sameDay.Count > 0 ? 2
                         : 1;

                var warning = tier switch
                {
                    4 => broken,
                    3 => $"Would be {run} days in a row",
                    2 => $"Also on the {sameDay[0].ShiftType} shift that day",
                    _ => null,
                };

                var familiarity = mine.Count(t =>
                    t.RouteId == trip.RouteId
                    && t.Date.Date >= day.AddDays(-SchedulingData.HistoryDays)
                    && t.Date.Date < day
                    && !Missed(t, s.Now));

                var week = mine.Count(t =>
                    t.Date.Date >= weekStart && t.Date.Date <= weekEnd && !Missed(t, s.Now));

                var reason = string.Join(" · ",
                    tier == 1 ? "Free all day" : "Free this shift",
                    familiarity == 0
                        ? $"No trips on {routeName} in {SchedulingData.HistoryDays} days"
                        : $"{familiarity} {Plural(familiarity, "trip")} on {routeName} in {SchedulingData.HistoryDays} days",
                    $"{week} {Plural(week, "shift")} this week");

                var facts = new DriverFacts(familiarity, routeName, week, sameDay.FirstOrDefault()?.ShiftType, run);

                ranked.Add((new DriverCandidate(driver.UserId, name, 0, tier, reason, warning, facts), familiarity, week));
            }

            var ordered = ranked
                .OrderBy(r => r.C.Tier)
                .ThenByDescending(r => r.Familiarity)
                .ThenBy(r => r.Week)
                .ThenBy(r => r.C.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.C.DriverId)
                .Select((r, i) => r.C with { Rank = i + 1 })
                .ToList();

            return new DriverRanking(
                ordered,
                onLeave.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                new DriverExclusions(onThatShift, unavailable));
        }

        // Buses that could take this trip instead of the one on it, best first.
        //
        // Never ranked: the bus already on the trip, a retired bus, a bus out of
        // service, and a bus already booked on the same shift that day.
        //
        // Tiers, best first:
        // 1. Based on the trip's route.
        // 2. Based on another route, or on none.
        // 3. Carries an open maintenance incident that did not ground it.
        //
        // Inside a tier: at least as many seats as the bus it replaces, then fewest
        // trips this week, then bus ID.
        //
        // Maintenance recency and mileage are deliberately absent. There is no
        // odometer, and nothing keeps the last maintenance date reliably current, so a
        // ranking on either would be a ranking on noise.
        public static VehicleRanking RankVehicles(Trip trip, SchedulingSnapshot s)
        {
            var day = trip.Date.Date;
            var others = s.Trips.Where(t => t.TripId != trip.TripId).ToList();
            var (weekStart, weekEnd) = WeekOf(day);

            var replacing = s.Vehicles.FirstOrDefault(v => Ci.Equals(v.VehicleId, trip.VehicleId));
            var seatsNeeded = replacing?.Capacity ?? 0;

            var ranked = new List<(VehicleCandidate C, bool SeatsOk, int Week)>();
            int onThatShift = 0, outOfService = 0;

            foreach (var v in s.Vehicles)
            {
                if (Ci.Equals(v.VehicleId, trip.VehicleId)) continue;
                if (v.RetiredAt != null) continue;
                if (v.OutOfService)
                {
                    outOfService++;
                    continue;
                }
                if (others.Any(t => t.Date.Date == day
                                 && Ci.Equals(t.ShiftType, trip.ShiftType)
                                 && Ci.Equals(t.VehicleId, v.VehicleId)))
                {
                    onThatShift++;
                    continue;
                }

                s.OpenIncidents.TryGetValue(v.VehicleId, out var incident);

                var tier = incident is not null ? 3
                         : v.RouteId == trip.RouteId ? 1
                         : 2;

                var seatsOk = v.Capacity >= seatsNeeded;
                var week = others.Count(t =>
                    Ci.Equals(t.VehicleId, v.VehicleId)
                    && t.Date.Date >= weekStart && t.Date.Date <= weekEnd
                    && !Missed(t, s.Now));

                var home = v.RouteId is null ? "No home route" : $"Based on {RouteName(s, v.RouteId.Value)}";
                var seats = seatsOk ? $"{v.Capacity} seats" : $"{v.Capacity} seats, fewer than {trip.VehicleId}";

                var reason = string.Join(" · ",
                    "Free this shift", home, seats, $"{week} {Plural(week, "trip")} this week");

                var warning = incident is null ? null : $"Needs attention: {IncidentSummary(incident)}";

                var facts = new VehicleFacts(
                    v.Capacity,
                    seatsOk ? null : trip.VehicleId,
                    v.RouteId is null ? null : RouteName(s, v.RouteId.Value),
                    v.RouteId == trip.RouteId,
                    week);

                ranked.Add((new VehicleCandidate(v.VehicleId, v.PlateNumber ?? "", 0, tier, reason, warning, facts),
                            seatsOk, week));
            }

            var ordered = ranked
                .OrderBy(r => r.C.Tier)
                .ThenByDescending(r => r.SeatsOk)
                .ThenBy(r => r.Week)
                .ThenBy(r => r.C.VehicleId, StringComparer.Ordinal)
                .Select((r, i) => r.C with { Rank = i + 1 })
                .ToList();

            return new VehicleRanking(ordered, new VehicleExclusions(onThatShift, outOfService));
        }

        // The worst tier a pick can come from and still cost nothing worth a warning.
        public const int NoCostTier = 2;

        // Why no driver can take a trip at no cost, or null when one can.
        //
        // Said in counts, because what the dispatcher does next depends on why: drivers who
        // would each break a rule are a decision to make, and drivers who are all booked are
        // a plan to change. "No driver is free without a cost: 2 would break a rest rule." or
        // "No driver can take this shift: 9 are already on that shift, 1 reported unable to
        // drive."
        public static string? DriverShortfall(DriverRanking ranking)
        {
            if (ranking.Candidates.Any(c => c.Tier <= NoCostTier)) return null;

            var parts = new List<string>();

            if (ranking.Candidates.Count > 0)
            {
                var rest = ranking.Candidates.Count(c => c.Tier == 4);
                var run = ranking.Candidates.Count(c => c.Tier == 3);
                if (rest > 0) parts.Add($"{rest} would break a rest rule");
                if (run > 0) parts.Add($"{run} would work 7 or more days in a row");
                return "No driver is free without a cost: " + JoinList(parts) + ".";
            }

            var excluded = ranking.Excluded ?? new DriverExclusions(0, 0);
            if (excluded.OnThatShift > 0) parts.Add($"{excluded.OnThatShift} {IsAre(excluded.OnThatShift)} already on that shift");
            if (excluded.ReportedUnavailable > 0) parts.Add($"{excluded.ReportedUnavailable} reported unable to drive");
            if (ranking.OnLeave.Count > 0) parts.Add($"{ranking.OnLeave.Count} {IsAre(ranking.OnLeave.Count)} on approved leave");

            return parts.Count == 0
                ? "No driver can take this shift."
                : "No driver can take this shift: " + JoinList(parts) + ".";
        }

        // Why no bus can take a trip at no cost, or null when one can.
        public static string? VehicleShortfall(VehicleRanking ranking)
        {
            if (ranking.Candidates.Any(c => c.Tier <= NoCostTier)) return null;

            if (ranking.Candidates.Count > 0)
            {
                var attention = ranking.Candidates.Count;
                return $"No bus is free without a cost: {attention} {(attention == 1 ? "needs" : "need")} attention.";
            }

            var parts = new List<string>();
            if (ranking.Excluded.OnThatShift > 0)
                parts.Add($"{ranking.Excluded.OnThatShift} {IsAre(ranking.Excluded.OnThatShift)} already on that shift");
            if (ranking.Excluded.OutOfService > 0)
                parts.Add($"{ranking.Excluded.OutOfService} {IsAre(ranking.Excluded.OutOfService)} out of service");

            return parts.Count == 0
                ? "No bus can take this trip."
                : "No bus can take this trip: " + JoinList(parts) + ".";
        }

        // A driver pick in one line, for where a single pick is shown rather than a list:
        // "Free all day, 2 North Loop trips in the last 30 days".
        //
        // Only the facts that decided the pick: whether the day is otherwise free, and how
        // well they know the route. The full figures belong in a list, where they can be
        // compared.
        public static string PickSentence(DriverCandidate c)
        {
            if (c.Facts is not { } f) return c.Reason;

            var free = c.Tier == 1 ? "Free all day" : "Free this shift";
            var route = f.RouteTrips == 0
                ? $"no {f.RouteName} trips in the last {SchedulingData.HistoryDays} days"
                : $"{f.RouteTrips} {f.RouteName} {Plural(f.RouteTrips, "trip")} in the last {SchedulingData.HistoryDays} days";

            return $"{free}, {route}";
        }

        // A bus pick in one line: "Based on North Loop, 40 seats".
        public static string PickSentence(VehicleCandidate c)
        {
            if (c.Facts is not { } f) return c.Reason;

            var home = f.HomeRoute is null ? "No home route"
                     : f.SameRoute ? $"Based on {f.HomeRoute}"
                     : $"From {f.HomeRoute}";
            var seats = f.FewerSeatsThan is null
                ? $"{f.Seats} seats"
                : $"{f.Seats} seats, fewer than {f.FewerSeatsThan}";

            return $"{home}, {seats}";
        }

        // A shift nobody is on yet, as a trip, for ranking who could take it.
        //
        // Carries no trip ID and no driver, so nobody is left out of the ranking for already
        // holding it.
        public static Trip OpenSlot(DateTime day, string shift, int routeId, string vehicleId)
        {
            var window = TripStatus.Windows[shift];
            return new Trip
            {
                TripId = "",
                Date = day.Date,
                ShiftType = shift,
                ShiftStartTime = window.Start,
                ShiftEndTime = window.End,
                RouteId = routeId,
                VehicleId = vehicleId,
                DriverId = 0,
                TripStatus = "Not Yet Started",
            };
        }

        // Where a driver booked into an empty shift sat in the ranking for it.
        //
        // For a roster gap filled from the Roster page or the planner. Worked out on the server
        // against the schedule as it stood before the booking, like
        // TagReassignment. An empty shift has nothing wrong with an existing
        // assignment, so no issues are recorded.
        public static ReassignmentTag TagFill(Trip slot, int driverId, SchedulingSnapshot s, string screen)
        {
            var ranking = RankDrivers(slot, s).Candidates;
            var pick = ranking.FirstOrDefault(c => c.DriverId == driverId);

            return new ReassignmentTag(
                pick?.Rank == 1,
                Array.Empty<string>(),
                new RankedPick(pick?.Rank, pick?.Tier, ranking.Count),
                null,
                screen);
        }

        // Where the driver and bus a reassignment chose sat in the rankings for the trip as
        // it stood before the change. Null when neither changed.
        //
        // Worked out on the server at the moment of saving, never taken from the browser,
        // so the figure reported on the Reports page cannot be written by the page it
        // measures.
        public static ReassignmentTag? TagReassignment(
            Trip before, int? newDriverId, string? newVehicleId, SchedulingSnapshot s)
        {
            var driverChanged = newDriverId.HasValue && newDriverId.Value != before.DriverId;
            var vehicleChanged = !string.IsNullOrEmpty(newVehicleId) && !Ci.Equals(newVehicleId, before.VehicleId);
            if (!driverChanged && !vehicleChanged) return null;

            var top = true;
            RankedPick? driver = null, vehicle = null;

            if (driverChanged)
            {
                var ranking = RankDrivers(before, s).Candidates;
                var pick = ranking.FirstOrDefault(c => c.DriverId == newDriverId);
                driver = new RankedPick(pick?.Rank, pick?.Tier, ranking.Count);
                top &= pick?.Rank == 1;
            }

            if (vehicleChanged)
            {
                var ranking = RankVehicles(before, s).Candidates;
                var pick = ranking.FirstOrDefault(c => Ci.Equals(c.VehicleId, newVehicleId));
                vehicle = new RankedPick(pick?.Rank, pick?.Tier, ranking.Count);
                top &= pick?.Rank == 1;
            }

            return new ReassignmentTag(top, IssuesOf(before, s), driver, vehicle);
        }

        // The worst tier a cover arranged from the leave queue may come from.
        //
        // Covers there are saved together under one button. A cover that makes a driver
        // work a seventh day in a row, or breaks a rest rule, deserves a decision of its
        // own, so it is left for the planner or the board, where the cost is confirmed.
        public const int CoverMaxTier = NoCostTier;

        // The pinned driver of a shift the dispatcher is leaving uncovered for now.
        public const int LeftForNow = 0;

        // The schedule as it would stand with this leave granted.
        //
        // A request still waiting is not leave yet, so a plain snapshot does not say the
        // driver is away. Ranking and the audit tag both need it to, or a cover for leave
        // would be recorded as a reassignment with nothing wrong.
        public static SchedulingSnapshot AsIfGranted(SchedulingSnapshot s, LeaveRequest request)
        {
            var granted = new LeaveRequest
            {
                RequestId = request.RequestId,
                UserId = request.UserId,
                LeaveType = request.LeaveType,
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                RevokedDates = request.RevokedDates,
                Status = "Approved",
            };

            return s with
            {
                Leave = s.Leave.Where(l => l.RequestId != request.RequestId).Append(granted).ToList(),
            };
        }

        // The schedule with drivers counted as on a shift they have been placed on in a plan
        // not saved yet.
        //
        // Each placement is a trip of its own that nothing else can mistake for a real one or
        // for the slot being ranked, so a placed driver is left out as already on that shift,
        // and counted as such in the reason nobody fits.
        public static SchedulingSnapshot WithPlaced(SchedulingSnapshot s, DateTime day, string shift, IEnumerable<int> driverIds)
        {
            var window = TripStatus.Windows[shift];
            var placed = driverIds
                .Where(id => id > 0)
                .Distinct()
                .Select(id => new Trip
                {
                    TripId = $"placed:{day:yyyy-MM-dd}:{shift}:{id}",
                    Date = day.Date,
                    ShiftType = shift,
                    ShiftStartTime = window.Start,
                    ShiftEndTime = window.End,
                    DriverId = id,
                    TripStatus = "Not Yet Started",
                })
                .ToList();

            return placed.Count == 0 ? s : s with { Trips = s.Trips.Concat(placed).ToList() };
        }

        // The schedule with one trip handed to another driver.
        public static SchedulingSnapshot WithDriver(SchedulingSnapshot s, string tripId, int driverId) =>
            s with
            {
                Trips = s.Trips.Select(t => t.TripId == tripId ? Reassigned(t, driverId) : t).ToList(),
            };

        // A cover for each shift, suggested in date order, each one ranked as though the
        // covers suggested before it were already saved.
        //
        // Ranked one at a time rather than all at once. A driver free all week would
        // otherwise top every shift of it and be suggested for a Morning and the Afternoon
        // straight after, a pairing the rules refuse the moment the first is saved.
        //
        // A shift the dispatcher has already chosen a driver for keeps that driver,
        // whatever the ranking now says, and the shifts after it are ranked around the
        // choice. When the choice can no longer be saved, the shift says why instead of
        // swapping in someone else. A shift left uncovered for now is suggested nobody, and
        // the shifts after it are ranked as though nobody were on it.
        // pinned: Drivers already chosen, by trip ID; LeftForNow for a shift left uncovered.
        public static IReadOnlyList<CoverShift> SuggestCovers(
            IEnumerable<Trip> shifts, SchedulingSnapshot s, IReadOnlyDictionary<string, int>? pinned = null)
        {
            var covers = new List<CoverShift>();

            foreach (var trip in shifts.OrderBy(t => t.Date).ThenBy(t => t.ShiftStartTime))
            {
                var ranking = RankDrivers(trip, s);
                var candidates = ranking.Candidates
                    .Where(c => c.Tier <= CoverMaxTier)
                    .ToList();
                var shortfall = candidates.Count == 0 ? DriverShortfall(ranking) : null;

                if (pinned is not null && pinned.TryGetValue(trip.TripId, out var chosen))
                {
                    if (chosen == LeftForNow)
                    {
                        covers.Add(new CoverShift(trip, candidates, null, shortfall, LeftForNow));
                        continue;
                    }

                    covers.Add(new CoverShift(
                        trip,
                        candidates,
                        candidates.FirstOrDefault(c => c.DriverId == chosen),
                        shortfall,
                        chosen,
                        PinnedProblemOf(ranking, chosen)));

                    s = WithDriver(s, trip.TripId, chosen);
                    continue;
                }

                var suggested = candidates.FirstOrDefault();

                covers.Add(new CoverShift(trip, candidates, suggested, shortfall));

                if (suggested is not null)
                    s = WithDriver(s, trip.TripId, suggested.DriverId);
            }

            return covers;
        }

        // Why a driver cannot be saved as cover for a trip from the leave queue, or null
        // when they can.
        //
        // Asked again at save time against the schedule as it stands, whatever the page
        // offered, so a list left open while the week changed cannot book a driver who has
        // since been given the shift beside it.
        public static string? CoverRefusal(Trip trip, int driverId, SchedulingSnapshot s) =>
            RefusalFrom(RankDrivers(trip, s), trip, driverId, s);

        // Why a driver the dispatcher has chosen for a shift could not be saved with it, said
        // as it stands before anything is saved, or null when they could.
        private static string? PinnedProblemOf(DriverRanking ranking, int driverId)
        {
            var pick = ranking.Candidates.FirstOrDefault(c => c.DriverId == driverId);

            if (pick is null)
                return "No longer free for this shift.";

            if (pick.Tier > CoverMaxTier)
                return $"{pick.Warning}. A cover with a cost is confirmed from the planner.";

            return null;
        }

        private static string? RefusalFrom(DriverRanking ranking, Trip trip, int driverId, SchedulingSnapshot s)
        {
            var shift = $"the {trip.ShiftType} shift on {trip.Date:MMM d}";
            var driver = s.Drivers.FirstOrDefault(d => d.UserId == driverId);
            var name = driver is null ? $"Driver {driverId}" : LabelOf(driver, s);

            var pick = ranking.Candidates.FirstOrDefault(c => c.DriverId == driverId);

            if (pick is null)
                return $"{name} is not free for {shift}.";

            if (pick.Tier > CoverMaxTier)
                return $"{name} was not put on {shift}: {pick.Warning}. "
                     + "A cover with a cost is confirmed on its own, from the planner.";

            return null;
        }

        // A copy of a trip with another driver on it, carrying the fields the rules read.
        private static Trip Reassigned(Trip t, int driverId) => new()
        {
            TripId = t.TripId,
            Date = t.Date,
            ShiftType = t.ShiftType,
            ShiftStartTime = t.ShiftStartTime,
            ShiftEndTime = t.ShiftEndTime,
            RouteId = t.RouteId,
            VehicleId = t.VehicleId,
            DriverId = driverId,
            TripStatus = t.TripStatus,
            ActualStartTime = t.ActualStartTime,
            ActualEndTime = t.ActualEndTime,
        };

        // The rest rule this shift would break for a driver, or null.
        //
        // The same three cases the assignment check refuses without an override: the shift
        // straight after or straight before on the same day, and an Evening shift that ends
        // as a Morning shift begins.
        private static string? RestRuleBroken(string shift, DateTime day, List<Trip> mine)
        {
            foreach (var t in mine.Where(t => t.Date.Date == day))
            {
                if (NextShift.TryGetValue(shift, out var after) && Ci.Equals(t.ShiftType, after))
                    return $"Runs straight into their {after} shift";
                if (NextShift.TryGetValue(t.ShiftType, out var before) && Ci.Equals(before, shift))
                    return $"Comes straight off their {t.ShiftType} shift";
            }

            if (Ci.Equals(shift, "Evening")
                && mine.Any(t => t.Date.Date == day.AddDays(1) && Ci.Equals(t.ShiftType, "Morning")))
                return "Starts a Morning shift the moment this one ends";

            if (Ci.Equals(shift, "Morning")
                && mine.Any(t => t.Date.Date == day.AddDays(-1) && Ci.Equals(t.ShiftType, "Evening")))
                return "Comes straight off an Evening shift the night before";

            return null;
        }

        // How many days in a row the driver would be working if they took a shift on this
        // day, counting it.
        //
        // A day counts when the driver has a trip on it that is not missed. Followed at most
        // a week either way, which is as far as the snapshot is guaranteed to reach.
        private static int ConsecutiveRun(DateTime day, List<Trip> mine, DateTime now)
        {
            bool Works(DateTime d) => mine.Any(t => t.Date.Date == d && !Missed(t, now));

            var back = 0;
            while (back < SchedulingData.RunDays && Works(day.AddDays(-(back + 1)))) back++;

            var forward = 0;
            while (forward < SchedulingData.RunDays && Works(day.AddDays(forward + 1))) forward++;

            return back + forward + 1;
        }

        // A trip whose window closed without it ever starting.
        private static bool Missed(Trip t, DateTime now) =>
            !Ci.Equals(t.TripStatus, "Active")
            && !Ci.Equals(t.TripStatus, "Completed")
            && TripStatus.Closed(t, now);

        // Monday to Sunday around a day.
        private static (DateTime Start, DateTime End) WeekOf(DateTime day)
        {
            var start = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
            return (start, start.AddDays(6));
        }

        private static bool IsActive(UserModel driver) =>
            Ci.Equals(driver.AccountStatus, "Activated");

        private static string NameOf(UserModel d)
        {
            var name = $"{d.FirstName} {d.LastName}".Trim();
            return name.Length == 0 ? $"Driver {d.UserId}" : name;
        }

        // A driver's name as a list shows it, with their ID added when another driver
        // account carries the same name.
        //
        // Names repeat. Two identical entries leave a dispatcher guessing which account
        // they are booking, and an entry matching the driver on the trip reads as that
        // driver being offered to replace themselves.
        private static string LabelOf(UserModel d, SchedulingSnapshot s)
        {
            var name = NameOf(d);
            var shared = s.Drivers.Any(o => o.UserId != d.UserId && Ci.Equals(NameOf(o), name));
            return shared ? $"{name} ({d.UserId})" : name;
        }

        private static string RouteName(SchedulingSnapshot s, int routeId) =>
            s.RouteNames.TryGetValue(routeId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"route {routeId}";

        private static string IncidentSummary(MaintenanceLog incident) =>
            incident.IssueDetails?.Issues is { Count: > 0 } issues
                ? string.Join(", ", issues)
                : "an open maintenance issue";

        private static string Plural(int n, string word) => n == 1 ? word : word + "s";

        private static string IsAre(int n) => n == 1 ? "is" : "are";

        // "a", "a and b", "a, b and c".
        private static string JoinList(IReadOnlyList<string> parts) => parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }
}
