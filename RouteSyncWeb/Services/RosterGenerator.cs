using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>Which roster wrote a trip, and whether a person has changed it since.</summary>
    public sealed record TripRosterMark(DateTime? RosterMonth, bool HandEdited);

    /// <summary>Everything a publish plan is worked out from.</summary>
    public sealed class RosterWorld
    {
        /// <summary>The first day of the month being published.</summary>
        public required DateTime Month { get; init; }

        /// <summary>Today's service day. Nothing on or before it is written or rewritten.</summary>
        public required DateTime OperationalDay { get; init; }

        public required DateTime Now { get; init; }

        /// <summary>The month's saved roster.</summary>
        public required IReadOnlyList<RosterSeat> Seats { get; init; }

        /// <summary>Trips from a week before the month to a week after it.</summary>
        public required IReadOnlyList<Trip> Trips { get; init; }

        /// <summary>The roster marks of those trips, by trip id. A trip missing here was made by hand.</summary>
        public required IReadOnlyDictionary<string, TripRosterMark> Marks { get; init; }

        public required IReadOnlyList<UserModel> Drivers { get; init; }
        public required IReadOnlyList<Vehicle> Vehicles { get; init; }

        /// <summary>Approved leave overlapping the month.</summary>
        public required IReadOnlyList<LeaveRequest> Leave { get; init; }

        /// <summary>Slots that must stay empty, as (date, bus, shift).</summary>
        public required IReadOnlySet<(DateTime Date, string VehicleId, string Shift)> Skips { get; init; }

        public required IReadOnlyDictionary<int, string> RouteNames { get; init; }
    }

    public sealed record PlannedInsert(
        DateTime Date, string Shift, int RouteId, string VehicleId, int DriverId,
        TimeSpan Start, TimeSpan End, TimeSpan BreakStart, bool IsCover);

    public sealed record PlannedUpdate(string TripId, DateTime Date, string Shift, string VehicleId, int DriverId, TimeSpan BreakStart, bool IsCover);

    public sealed record PlannedGap(DateTime Date, string VehicleId, int RouteId, string Shift, string Reason);

    /// <summary>A roster slot a publish leaves as a person left it.</summary>
    /// <param name="ByHand">True for a roster trip edited by hand; false for a trip made by hand in a roster slot.</param>
    public sealed record KeptTrip(string TripId, DateTime Date, string Shift, string VehicleId, int DriverId, bool ByHand);

    /// <summary>A crew driver with nothing to drive on a shift, because a trip made by hand took their bus.</summary>
    public sealed record IdleDriver(int DriverId, DateTime Date, string Shift, string VehicleId);

    /// <summary>What a publish would write, and what it would leave and why.</summary>
    public sealed class PublishPlan
    {
        public List<PlannedInsert> Inserts { get; } = new();
        public List<PlannedUpdate> Updates { get; } = new();
        public List<string> Deletes { get; } = new();
        public List<PlannedGap> Gaps { get; } = new();
        public List<KeptTrip> Kept { get; } = new();
        public List<IdleDriver> Idle { get; } = new();

        /// <summary>Roster trips that already match the plan and need no write.</summary>
        public int Unchanged { get; set; }

        /// <summary>Slots left empty because they were skipped.</summary>
        public int Skipped { get; set; }

        /// <summary>Rest days and absences filled from the route's floaters.</summary>
        public int CoversFilled => Inserts.Count(i => i.IsCover) + Updates.Count(u => u.IsCover);

        /// <summary>Buses out of service now, and how many of the month's written trips use them.</summary>
        public Dictionary<string, int> OutOfServiceUse { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The first day the plan covers.</summary>
        public DateTime From { get; set; }

        /// <summary>The last day of the month.</summary>
        public DateTime To { get; set; }
    }

    /// <summary>
    /// Expands a month's roster into its trips, and rotates a roster into the next month.
    /// </summary>
    /// <remarks>
    /// <para>Pure. The dashboard gathers a <see cref="RosterWorld"/>, asks for a plan, shows
    /// it, and hands it to publish_roster_month, which checks every row of it again before
    /// writing.</para>
    ///
    /// <para>For each day after today, in the order Morning, Afternoon, Evening, and for
    /// each bus and shift the roster runs:</para>
    /// <list type="number">
    /// <item>A skipped slot gets nothing.</item>
    /// <item>A slot holding a trip made or edited by hand, or already started, keeps it.</item>
    /// <item>A slot on a retired bus becomes a gap.</item>
    /// <item>The crew driver takes it when free: not resting, not on leave, active, not
    /// booked on that shift, breaking no rest rule, and not into a seventh day running.</item>
    /// <item>Otherwise one of the route's floaters free by the same test takes it, the
    /// floater whose home shift it is first. None free makes it a gap, with why.</item>
    /// </list>
    /// <para>A roster trip still untouched is rewritten in place when the plan for its slot
    /// changed, removed when its slot no longer runs or cannot be filled, and left alone
    /// when it already matches.</para>
    /// </remarks>
    public static class RosterGenerator
    {
        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

        /// <summary>Backward: each month's shift is the one before it in the day.</summary>
        private static readonly string[] Backward = { "Morning", "Evening", "Afternoon" };

        private static readonly Dictionary<string, string> NextShift = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Morning"] = "Afternoon",
            ["Afternoon"] = "Evening",
        };

        private const int MaxRun = 6;

        /// <summary>
        /// A month's roster carried into the next: the same buses, routes and rest days, each
        /// shift moved one step backward within the shifts its bus runs.
        /// </summary>
        /// <remarks>
        /// Backward, Morning to Evening to Afternoon to Morning, because it is the one
        /// direction that leaves eight hours between the last shift of one month and the first
        /// of the next. A crew of two on Morning and Afternoon swaps. A floater's home shift
        /// moves the same way within the shifts its route runs.
        /// </remarks>
        public static IReadOnlyList<RosterSeat> Rotate(IReadOnlyList<RosterSeat> seats)
        {
            var busShifts = seats.Where(s => s.Kind == RosterRules.Crew && s.VehicleId != null)
                .GroupBy(s => s.VehicleId!, Ci)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Shift).ToHashSet(Ci), Ci);

            var routeShifts = seats.Where(s => s.Kind == RosterRules.Crew)
                .GroupBy(s => s.RouteId)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Shift).ToHashSet(Ci));

            return seats.Select(s =>
            {
                var runs = s.Kind == RosterRules.Crew
                    ? busShifts.GetValueOrDefault(s.VehicleId ?? "")
                    : routeShifts.GetValueOrDefault(s.RouteId);
                return s with { Shift = StepBack(s.Shift, runs) };
            }).ToList();
        }

        private static string StepBack(string shift, HashSet<string>? runs)
        {
            if (runs is null || runs.Count == 0) return shift;
            var i = Array.FindIndex(Backward, b => Ci.Equals(b, shift));
            if (i < 0) return shift;
            for (var step = 1; step <= Backward.Length; step++)
            {
                var candidate = Backward[(i + step) % Backward.Length];
                if (runs.Contains(candidate)) return candidate;
            }
            return shift;
        }

        /// <summary>The trips a publish of the month would write, rewrite, remove and keep.</summary>
        public static PublishPlan Plan(RosterWorld w)
        {
            var monthEnd = w.Month.AddMonths(1).AddDays(-1);
            var first = w.Month > w.OperationalDay ? w.Month : w.OperationalDay.AddDays(1);
            var plan = new PublishPlan { From = first, To = monthEnd };
            if (first > monthEnd) return plan;

            var busById = w.Vehicles.ToDictionary(v => v.VehicleId, Ci);
            var driverById = w.Drivers.ToDictionary(d => d.UserId);
            var seats = w.Seats.Where(s => s.DriverId is not null && s.RestWeekday is not null).ToList();

            bool Regenerable(Trip t) =>
                w.Marks.TryGetValue(t.TripId, out var m)
                && m.RosterMonth?.Date == w.Month.Date
                && !m.HandEdited
                && !Ci.Equals(t.TripStatus, "Active")
                && !Ci.Equals(t.TripStatus, "Completed")
                && t.Date.Date > w.OperationalDay.Date;

            bool InPlan(Trip t) => t.Date.Date >= first && t.Date.Date <= monthEnd;

            // What stays whatever the plan says: every trip outside the planned days, and every
            // trip inside them that a publish may not rewrite.
            var book = new Bookings();
            foreach (var t in w.Trips.Where(t => !(InPlan(t) && Regenerable(t))))
                book.Add(t.DriverId, t.Date.Date, t.ShiftType, Missed(t, w.Now));

            var existing = w.Trips.Where(InPlan)
                .GroupBy(t => (Day: t.Date.Date, Shift: t.ShiftType.ToUpperInvariant(), Bus: t.VehicleId.ToUpperInvariant()))
                .ToDictionary(g => g.Key, g => g.First());

            var touched = new HashSet<string>();
            var skips = w.Skips.Select(s => (s.Date.Date, s.VehicleId.ToUpperInvariant(), s.Shift.ToUpperInvariant())).ToHashSet();

            // Break slots by the bus's place among its route's buses on that shift, from the
            // roster rather than the day's trips, so a skipped bus does not move everyone else's.
            var breakIndex = seats.Where(s => s.Kind == RosterRules.Crew)
                .GroupBy(s => (s.RouteId, Shift: s.Shift.ToUpperInvariant()))
                .SelectMany(g => g.OrderBy(s => s.VehicleId, StringComparer.Ordinal).Select((s, i) => (s, i)))
                .ToDictionary(x => (x.s.VehicleId!.ToUpperInvariant(), x.s.Shift.ToUpperInvariant()), x => x.i);

            string? Unavailable(int driverId, DateTime day, string shift, int restWeekday)
            {
                if (!driverById.TryGetValue(driverId, out var d) || !Ci.Equals(d.AccountStatus, "Activated"))
                    return "account not active";
                if (IsoWeekday(day) == restWeekday) return "rest day";
                if (w.Leave.Any(l => l.UserId == driverId && LeaveEntitlement.CoversDay(l, day))) return "on leave";
                if (book.Has(driverId, day, shift)) return "booked on another bus";
                if (BreaksRestRule(book, driverId, day, shift)) return "too soon after another shift";
                if (book.RunWith(driverId, day) > MaxRun) return "would work a seventh day running";
                return null;
            }

            for (var day = first; day <= monthEnd; day = day.AddDays(1))
            {
                foreach (var shift in RosterRules.Shifts)
                {
                    foreach (var seat in seats.Where(s => s.Kind == RosterRules.Crew && Ci.Equals(s.Shift, shift))
                                              .OrderBy(s => s.VehicleId, StringComparer.Ordinal))
                    {
                        var busId = seat.VehicleId!;
                        var key = (Day: day, Shift: shift.ToUpperInvariant(), Bus: busId.ToUpperInvariant());
                        existing.TryGetValue(key, out var trip);
                        if (trip is not null) touched.Add(trip.TripId);

                        if (skips.Contains((day, busId.ToUpperInvariant(), shift.ToUpperInvariant())))
                        {
                            plan.Skipped++;
                            if (trip is not null && Regenerable(trip)) plan.Deletes.Add(trip.TripId);
                            continue;
                        }

                        if (trip is not null && !Regenerable(trip))
                        {
                            // A trip already running or run is simply the day as it happened. The
                            // ones worth naming are those a person made or changed.
                            var mark = w.Marks.GetValueOrDefault(trip.TripId);
                            var started = Ci.Equals(trip.TripStatus, "Active") || Ci.Equals(trip.TripStatus, "Completed");
                            if (!started && (mark?.RosterMonth is null || mark.HandEdited))
                                plan.Kept.Add(new KeptTrip(trip.TripId, day, shift, busId, trip.DriverId, ByHand: mark?.RosterMonth is not null));

                            var crew = seat.DriverId!.Value;
                            if (trip.DriverId != crew && Unavailable(crew, day, shift, seat.RestWeekday!.Value) is null)
                                plan.Idle.Add(new IdleDriver(crew, day, shift, busId));
                            continue;
                        }

                        busById.TryGetValue(busId, out var bus);
                        if (bus is null || bus.RetiredAt != null)
                        {
                            plan.Gaps.Add(new PlannedGap(day, busId, seat.RouteId, shift, "Bus retired"));
                            if (trip is not null) plan.Deletes.Add(trip.TripId);
                            continue;
                        }

                        var crewDriver = seat.DriverId!.Value;
                        var crewReason = Unavailable(crewDriver, day, shift, seat.RestWeekday!.Value);

                        int? chosen = crewReason is null ? crewDriver : null;
                        var isCover = false;

                        if (chosen is null)
                        {
                            var floaters = seats.Where(s => s.Kind == RosterRules.Floater && s.RouteId == seat.RouteId).ToList();
                            var free = floaters
                                .Select(f => (f, why: Unavailable(f.DriverId!.Value, day, shift, f.RestWeekday!.Value)))
                                .ToList();

                            var pick = free.Where(x => x.why is null)
                                .OrderBy(x => Ci.Equals(x.f.Shift, shift) ? 0 : 1)
                                .ThenByDescending(x => Familiarity(w.Trips, x.f.DriverId!.Value, seat.RouteId, day))
                                .ThenBy(x => book.WeekCount(x.f.DriverId!.Value, day))
                                .ThenBy(x => NameOf(driverById, x.f.DriverId!.Value), Ci)
                                .ThenBy(x => x.f.DriverId)
                                .Select(x => (int?)x.f.DriverId)
                                .FirstOrDefault();

                            if (pick is null)
                            {
                                plan.Gaps.Add(new PlannedGap(day, busId, seat.RouteId, shift,
                                    GapReason(NameOf(driverById, crewDriver), crewReason!, free.Select(x => x.why))));
                                if (trip is not null) plan.Deletes.Add(trip.TripId);
                                continue;
                            }

                            chosen = pick;
                            isCover = true;
                        }

                        var window = TripStatus.Windows[shift];
                        var slots = BreakSlots.For(window.Start);
                        var breakStart = slots[breakIndex.GetValueOrDefault((busId.ToUpperInvariant(), shift.ToUpperInvariant())) % slots.Count];

                        book.Add(chosen.Value, day, shift, missed: false);

                        if (bus.OutOfService)
                            plan.OutOfServiceUse[busId] = plan.OutOfServiceUse.GetValueOrDefault(busId) + 1;

                        if (trip is null)
                        {
                            plan.Inserts.Add(new PlannedInsert(day, shift, seat.RouteId, busId, chosen.Value,
                                window.Start, window.End, breakStart, isCover));
                        }
                        else if (trip.DriverId != chosen.Value || trip.BreakStart != breakStart)
                        {
                            plan.Updates.Add(new PlannedUpdate(trip.TripId, day, shift, busId, chosen.Value, breakStart, isCover));
                        }
                        else
                        {
                            plan.Unchanged++;
                        }
                    }
                }
            }

            // Roster trips in slots the roster no longer runs.
            foreach (var t in existing.Values.Where(t => !touched.Contains(t.TripId) && Regenerable(t)))
                plan.Deletes.Add(t.TripId);

            return plan;
        }

        /// <summary>Why a slot went unfilled, in a line a dispatcher can act on.</summary>
        /// <example>Pedro Reyes on their rest day, no floater free (1 resting, 1 on leave)</example>
        private static string GapReason(string crewName, string crewReason, IEnumerable<string?> floaterReasons)
        {
            var counts = floaterReasons
                .Where(r => r is not null)
                .GroupBy(r => FloaterPhrase(r!))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Count()} {g.Key}")
                .ToList();

            var floaters = counts.Count == 0
                ? "the route has no floaters"
                : $"no floater free ({string.Join(", ", counts)})";

            return $"{crewName} {CrewPhrase(crewReason)}, {floaters}";
        }

        private static string CrewPhrase(string reason) => reason switch
        {
            "rest day" => "on their rest day",
            "account not active" => "not an active driver",
            _ => reason,
        };

        private static string FloaterPhrase(string reason) => reason switch
        {
            "rest day" => "resting",
            "on leave" => "on leave",
            "booked on another bus" => "already booked",
            "too soon after another shift" => "just off a shift",
            "would work a seventh day running" => "at six days running",
            "account not active" => "inactive",
            _ => reason,
        };

        private static bool BreaksRestRule(Bookings book, int driverId, DateTime day, string shift)
        {
            foreach (var other in book.ShiftsOn(driverId, day))
            {
                if (NextShift.TryGetValue(shift, out var after) && Ci.Equals(other, after)) return true;
                if (NextShift.TryGetValue(other, out var before) && Ci.Equals(before, shift)) return true;
            }

            if (Ci.Equals(shift, "Evening") && book.ShiftsOn(driverId, day.AddDays(1)).Contains("Morning", Ci)) return true;
            if (Ci.Equals(shift, "Morning") && book.ShiftsOn(driverId, day.AddDays(-1)).Contains("Evening", Ci)) return true;

            return false;
        }

        private static int Familiarity(IReadOnlyList<Trip> trips, int driverId, int routeId, DateTime day) =>
            trips.Count(t => t.DriverId == driverId && t.RouteId == routeId
                          && t.Date.Date < day && t.Date.Date >= day.AddDays(-SchedulingData.HistoryDays));

        private static bool Missed(Trip t, DateTime now) =>
            !Ci.Equals(t.TripStatus, "Active") && !Ci.Equals(t.TripStatus, "Completed") && TripStatus.Closed(t, now);

        private static int IsoWeekday(DateTime day) => day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;

        private static string NameOf(Dictionary<int, UserModel> drivers, int id)
        {
            if (!drivers.TryGetValue(id, out var d)) return $"Driver {id}";
            var name = $"{d.FirstName} {d.LastName}".Trim();
            return name.Length == 0 ? $"Driver {id}" : name;
        }

        /// <summary>Who is booked on which shift of which day, as the plan builds up.</summary>
        private sealed class Bookings
        {
            private readonly Dictionary<(int Driver, DateTime Day), List<string>> _shifts = new();
            private readonly HashSet<(int Driver, DateTime Day)> _worked = new();

            public void Add(int driverId, DateTime day, string shift, bool missed)
            {
                if (!_shifts.TryGetValue((driverId, day), out var list))
                    _shifts[(driverId, day)] = list = new List<string>();
                list.Add(shift);
                if (!missed) _worked.Add((driverId, day));
            }

            public IReadOnlyList<string> ShiftsOn(int driverId, DateTime day) =>
                _shifts.TryGetValue((driverId, day), out var list) ? list : Array.Empty<string>();

            public bool Has(int driverId, DateTime day, string shift) => ShiftsOn(driverId, day).Contains(shift, Ci);

            /// <summary>The days in a row the driver would work, counting this one.</summary>
            public int RunWith(int driverId, DateTime day)
            {
                var back = 0;
                while (back < MaxRun && _worked.Contains((driverId, day.AddDays(-(back + 1))))) back++;
                var forward = 0;
                while (forward < MaxRun && _worked.Contains((driverId, day.AddDays(forward + 1)))) forward++;
                return back + forward + 1;
            }

            /// <summary>Shifts in the Monday to Sunday week around a day.</summary>
            public int WeekCount(int driverId, DateTime day)
            {
                var start = day.AddDays(-((int)day.DayOfWeek + 6) % 7);
                return Enumerable.Range(0, 7).Sum(i => ShiftsOn(driverId, start.AddDays(i)).Count);
            }
        }
    }
}
