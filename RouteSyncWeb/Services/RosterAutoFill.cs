using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>How often each driver drove each bus and each route lately.</summary>
    /// <remarks>
    /// What auto-fill chooses by: the driver who knows the bus, then the route. Counted over
    /// whatever trips it is given, which is the last <see cref="SchedulingData.HistoryDays"/>
    /// days, whatever their status.
    /// </remarks>
    public sealed class RosterHistory
    {
        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

        private readonly Dictionary<(int Driver, string Bus), int> _bus = new();
        private readonly Dictionary<(int Driver, int Route), int> _route = new();
        private readonly Dictionary<int, int> _any = new();

        public RosterHistory(IEnumerable<Trip> trips)
        {
            foreach (var t in trips)
            {
                if (t.DriverId == 0) continue;

                var bus = (t.DriverId, (t.VehicleId ?? "").ToUpperInvariant());
                _bus[bus] = _bus.GetValueOrDefault(bus) + 1;

                var route = (t.DriverId, t.RouteId);
                _route[route] = _route.GetValueOrDefault(route) + 1;

                _any[t.DriverId] = _any.GetValueOrDefault(t.DriverId) + 1;
            }
        }

        public static RosterHistory None { get; } = new(Array.Empty<Trip>());

        public int OnBus(int driverId, string? vehicleId) =>
            _bus.GetValueOrDefault((driverId, (vehicleId ?? "").ToUpperInvariant()));

        public int OnRoute(int driverId, int routeId) => _route.GetValueOrDefault((driverId, routeId));

        public int Anywhere(int driverId) => _any.GetValueOrDefault(driverId);
    }

    /// <summary>A roster after auto-fill, and what it did, in sentences.</summary>
    /// <param name="Filled">Places given a driver.</param>
    /// <param name="Emptied">Places emptied or taken off the roster: an inactive driver, a retired or moved bus.</param>
    /// <param name="RestDaysSet">Rest days and floaters' usual shifts set where they were missing.</param>
    /// <param name="LeftEmpty">Places still without a driver, for want of anybody free.</param>
    /// <param name="Notes">Everything auto-fill did, a sentence each.</param>
    /// <param name="Unmarked">
    /// The notes that say what no place's mark says: places and rows that came off, crews
    /// released, and floaters nobody could be found for. The rest are said by a mark as well.
    /// </param>
    public sealed record AutoFillResult(
        IReadOnlyList<RosterSeat> Seats, IReadOnlyList<string> Notes, IReadOnlyList<string> Unmarked,
        int Filled, int Emptied, int RestDaysSet, int LeftEmpty)
    {
        public bool Changed => Filled + Emptied + RestDaysSet > 0;
    }

    public static partial class RosterRules
    {
        /// <summary>
        /// Clears a roster of drivers and buses that can no longer be on it, then fills its empty
        /// places by rule. Nothing is saved; the page shows the result for a person to review.
        /// </summary>
        /// <remarks>
        /// <para>Clean-up first:</para>
        /// <list type="bullet">
        /// <item>A crew place held by an inactive driver is emptied. An inactive floater's row goes.</item>
        /// <item>A retired bus, a bus with no route and a bus that no longer exists come off the
        /// roster, and their crew are released.</item>
        /// <item>A bus now based on another route takes the shifts it runs there, empty, and its
        /// crew are released.</item>
        /// </list>
        /// <para>Then only empty places are filled. A driver already validly placed keeps their
        /// bus, shift and rest day, and a person decides which shifts a bus runs.</para>
        /// <list type="number">
        /// <item>Empty crew places, from active drivers with no place. Each goes to the pairing
        /// with the most trips on that bus in the last 30 days, then on that route; a driver new
        /// to the fleet before one who knows only other routes; then the place's order on the
        /// page, the driver's name and id.</item>
        /// <item>Floaters, up to one for every six crew places on the route, chosen by trips on
        /// the route the same way. Crew places come first, because an empty crew place is a gap
        /// every day and a missing floater only on rest days.</item>
        /// <item>Rest days where missing: a floater on the weekday fewest of the route's floaters
        /// rest, a crew driver on the weekday with the most floater cover to spare, avoiding a
        /// crewmate's day. A new floater's usual shift goes where the route is shortest of them.
        /// Those new rest days are then settled against the planner, and moved where that
        /// uncovers less. A rest day a driver already had is never moved.</item>
        /// </list>
        /// <para>A driver held back is never placed. They stay available for one-off cover, and
        /// a person may still place them by hand.</para>
        /// <para>Every place it changes carries a sentence saying why, for the page to show.</para>
        /// </remarks>
        public static AutoFillResult AutoFill(
            IReadOnlyList<RosterSeat> seats,
            IReadOnlyList<Vehicle> vehicles,
            IReadOnlyList<UserModel> drivers,
            IReadOnlyDictionary<int, string> routeNames,
            RosterHistory history,
            IReadOnlySet<int>? held = null)
        {
            held ??= new HashSet<int>();
            var busById = new Dictionary<string, Vehicle>(Ci);
            foreach (var v in vehicles) busById.TryAdd(v.VehicleId, v);
            var driverById = new Dictionary<int, UserModel>();
            foreach (var d in drivers) driverById.TryAdd(d.UserId, d);

            // A name two accounts share carries the id, so a note says which of them it means.
            var shared = drivers.GroupBy(NameOf, Ci).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(Ci);
            string Label(UserModel d) => shared.Contains(NameOf(d)) ? $"{NameOf(d)} ({d.UserId})" : NameOf(d);
            string Name(int id) => driverById.TryGetValue(id, out var d) ? Label(d) : $"Driver {id}";
            string Route(int id) => routeNames.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"route {id}";
            bool Active(int id) => driverById.TryGetValue(id, out var d) && IsActiveDriver(d);

            var notes = new List<string>();
            var unmarked = new List<string>();
            var places = new List<Place>();

            // A note about something no place is left to carry a mark for.
            void NoteUnmarked(string note)
            {
                notes.Add(note);
                unmarked.Add(note);
            }
            var placed = new HashSet<int>();
            var busShifts = new HashSet<(string Bus, string Shift)>();
            int emptied = 0, filled = 0, restSet = 0, leftEmpty = 0;

            // Buses that came off the roster or moved, gathered so each is said once.
            var gone = new Dictionary<string, (string Why, List<string> Shifts, List<string> Released)>(Ci);
            var moved = new Dictionary<string, (string From, string To, List<string> Shifts, List<string> Released)>(Ci);

            // ---- 1. Clean up -------------------------------------------------------------------

            foreach (var s in seats)
            {
                if (s.Kind == Crew)
                {
                    var busId = s.VehicleId ?? "";
                    busById.TryGetValue(busId, out var bus);
                    var why = bus is null ? "no longer exists"
                            : bus.RetiredAt != null ? "is retired"
                            : bus.RouteId is null ? "has no route"
                            : null;

                    if (why is not null)
                    {
                        if (!gone.TryGetValue(busId, out var g)) gone[busId] = g = (why, new List<string>(), new List<string>());
                        g.Shifts.Add(s.Shift);
                        if (s.DriverId is int r) g.Released.Add(Name(r));
                        emptied++;
                        continue;
                    }

                    var seat = s;
                    var place = new Place(places.Count, seat);

                    if (bus!.RouteId != s.RouteId)
                    {
                        var to = bus.RouteId!.Value;
                        if (!moved.TryGetValue(bus.VehicleId, out var m))
                            moved[bus.VehicleId] = m = (Route(s.RouteId), Route(to), new List<string>(), new List<string>());
                        m.Shifts.Add(s.Shift);
                        if (s.DriverId is int r) { m.Released.Add(Name(r)); emptied++; }

                        seat = seat with { RouteId = to, DriverId = null, RestWeekday = null };
                        place.Said.Add($"Moved with {bus.VehicleId} from {Route(s.RouteId)}.");
                    }

                    if (!busShifts.Add((bus.VehicleId.ToUpperInvariant(), s.Shift)))
                    {
                        NoteUnmarked($"Removed a second {s.Shift} place on {bus.VehicleId}.");
                        emptied++;
                        continue;
                    }

                    if (seat.DriverId is int id)
                    {
                        if (!Active(id))
                        {
                            notes.Add($"Emptied {bus.VehicleId} {s.Shift}: {Name(id)} is no longer an active driver.");
                            place.Said.Add($"Replaces {Name(id)}, who is no longer an active driver.");
                            seat = seat with { DriverId = null, RestWeekday = null };
                            emptied++;
                        }
                        else if (!placed.Add(id))
                        {
                            notes.Add($"Emptied {bus.VehicleId} {s.Shift}: {Name(id)} already has a place.");
                            place.Said.Add($"{Name(id)} already had another place.");
                            seat = seat with { DriverId = null, RestWeekday = null };
                            emptied++;
                        }
                    }

                    place.Seat = seat;
                    places.Add(place);
                }
                else
                {
                    if (s.DriverId is int id)
                    {
                        if (!Active(id))
                        {
                            NoteUnmarked($"Removed {Name(id)} from the {Route(s.RouteId)} floaters: no longer an active driver.");
                            emptied++;
                            continue;
                        }
                        if (!placed.Add(id))
                        {
                            NoteUnmarked($"Removed a second place for {Name(id)} among the {Route(s.RouteId)} floaters.");
                            emptied++;
                            continue;
                        }
                    }

                    places.Add(new Place(places.Count, s));
                }
            }

            foreach (var (busId, g) in gone.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                NoteUnmarked($"{busId} {g.Why}, so its {Joined(g.Shifts)} {Plural(g.Shifts.Count, "place", "places")} came off the roster"
                    + (g.Released.Count > 0 ? $" and {Joined(g.Released)} {(g.Released.Count == 1 ? "was" : "were")} released." : "."));

            foreach (var (busId, m) in moved.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                NoteUnmarked($"{busId} now runs on {m.To}, so its {Joined(m.Shifts)} {Plural(m.Shifts.Count, "place", "places")} moved there from {m.From}"
                    + (m.Released.Count > 0 ? $" and {Joined(m.Released)} {(m.Released.Count == 1 ? "was" : "were")} released." : "."));

            var free = drivers
                .Where(d => IsActiveDriver(d) && !placed.Contains(d.UserId) && !held.Contains(d.UserId))
                .OrderBy(NameOf, Ci)
                .ThenBy(d => d.UserId)
                .ToList();

            string Trips(int n) => n == 1 ? "1 trip" : $"{n} trips";

            // A new driver comes before one who knows only other routes, who is likely wanted there.
            int Elsewhere(int driverId, int routeId) =>
                history.OnRoute(driverId, routeId) == 0 && history.Anywhere(driverId) > 0 ? 1 : 0;

            string Why(int driverId, int routeId, string? busId)
            {
                var onBus = busId is null ? 0 : history.OnBus(driverId, busId);
                var onRoute = history.OnRoute(driverId, routeId);
                return onBus > 0 ? $"{Trips(onBus)} on {busId} in the last {SchedulingData.HistoryDays} days."
                     : onRoute > 0 ? $"{Trips(onRoute)} on {Route(routeId)} in the last {SchedulingData.HistoryDays} days."
                     : history.Anywhere(driverId) > 0 ? $"Free, and new to {Route(routeId)}."
                     : $"New driver, with no trips in the last {SchedulingData.HistoryDays} days.";
            }

            // ---- 2. Crew places ----------------------------------------------------------------

            var emptyCrew = places.Where(p => p.Seat.Kind == Crew && p.Seat.DriverId is null).ToList();
            while (emptyCrew.Count > 0 && free.Count > 0)
            {
                var best = emptyCrew
                    .SelectMany(p => free.Select(d => (p, d)))
                    .OrderByDescending(x => history.OnBus(x.d.UserId, x.p.Seat.VehicleId))
                    .ThenByDescending(x => history.OnRoute(x.d.UserId, x.p.Seat.RouteId))
                    .ThenBy(x => Elsewhere(x.d.UserId, x.p.Seat.RouteId))
                    .ThenBy(x => x.p.Order)
                    .ThenBy(x => NameOf(x.d), Ci)
                    .ThenBy(x => x.d.UserId)
                    .First();

                var why = Why(best.d.UserId, best.p.Seat.RouteId, best.p.Seat.VehicleId);
                best.p.Seat = best.p.Seat with { DriverId = best.d.UserId };
                best.p.Said.Add(why);
                notes.Add($"Put {Label(best.d)} on {best.p.Seat.VehicleId} {best.p.Seat.Shift}. {why}");

                emptyCrew.Remove(best.p);
                free.Remove(best.d);
                filled++;
            }

            foreach (var p in emptyCrew)
            {
                p.Said.Add("No active driver is free for this place.");
                notes.Add($"Nobody is free for {p.Seat.VehicleId} {p.Seat.Shift}.");
                leftEmpty++;
            }

            // ---- 3. Floaters ------------------------------------------------------------------

            var routesOnRoster = places.Select(p => p.Seat.RouteId).Distinct().OrderBy(id => id).ToList();
            var added = new List<Place>();

            foreach (var routeId in routesOnRoster)
            {
                var crewPlaces = places.Count(p => p.Seat.Kind == Crew && p.Seat.RouteId == routeId);
                var needed = (int)Math.Ceiling(crewPlaces / 6.0);
                var rows = places.Count(p => p.Seat.Kind == Floater && p.Seat.RouteId == routeId);

                for (var k = rows; k < needed; k++)
                {
                    var row = new Place(places.Count, new RosterSeat(null, Floater, routeId, null, Shifts[0], null)) { NewFloater = true };
                    places.Add(row);
                    added.Add(row);
                }
            }

            var emptyFloaters = places.Where(p => p.Seat.Kind == Floater && p.Seat.DriverId is null).ToList();
            while (emptyFloaters.Count > 0 && free.Count > 0)
            {
                var best = emptyFloaters
                    .SelectMany(p => free.Select(d => (p, d)))
                    .OrderByDescending(x => history.OnRoute(x.d.UserId, x.p.Seat.RouteId))
                    .ThenBy(x => Elsewhere(x.d.UserId, x.p.Seat.RouteId))
                    .ThenBy(x => x.p.Order)
                    .ThenBy(x => NameOf(x.d), Ci)
                    .ThenBy(x => x.d.UserId)
                    .First();

                var why = Why(best.d.UserId, best.p.Seat.RouteId, null);
                best.p.Seat = best.p.Seat with { DriverId = best.d.UserId };
                best.p.Said.Add(why);
                notes.Add($"Added {Label(best.d)} as a {Route(best.p.Seat.RouteId)} floater. {why}");

                emptyFloaters.Remove(best.p);
                free.Remove(best.d);
                filled++;
            }

            // A floater row nobody could take is not worth adding. One a person added is left for
            // them, and the save says what it lacks.
            foreach (var p in emptyFloaters)
            {
                if (p.NewFloater)
                {
                    places.Remove(p);
                    NoteUnmarked($"{Route(p.Seat.RouteId)} needs another floater and no active driver is free.");
                }
                else
                {
                    NoteUnmarked($"No active driver is free for the empty {Route(p.Seat.RouteId)} floater row.");
                    leftEmpty++;
                }
            }

            // ---- 4. Rest days and usual shifts, where missing ----------------------------------

            var justSet = new HashSet<Place>();

            foreach (var routeId in routesOnRoster)
            {
                var floaters = places.Where(p => p.Seat.Kind == Floater && p.Seat.RouteId == routeId && p.Seat.DriverId is not null).ToList();
                var crew = places.Where(p => p.Seat.Kind == Crew && p.Seat.RouteId == routeId && p.Seat.DriverId is not null)
                                 .OrderBy(p => p.Seat.VehicleId, StringComparer.Ordinal)
                                 .ThenBy(p => ShiftOrder(p.Seat.Shift))
                                 .ToList();

                var floatersResting = Weekdays.ToDictionary(d => d, d => floaters.Count(f => f.Seat.RestWeekday == d));
                var crewResting = Weekdays.ToDictionary(d => d, d => crew.Count(c => c.Seat.RestWeekday == d));

                foreach (var f in floaters.Where(f => f.Seat.RestWeekday is null))
                {
                    var day = Weekdays
                        .OrderBy(d => floatersResting[d])
                        .ThenBy(d => crewResting[d])
                        .ThenBy(d => d)
                        .First();

                    f.Seat = f.Seat with { RestWeekday = day };
                    f.Said.Add($"{RestDayMark}{DayName(day)}, the day fewest {Route(routeId)} floaters rest.");
                    floatersResting[day]++;
                    justSet.Add(f);
                    restSet++;
                }

                var covering = Weekdays.ToDictionary(d => d, d => floaters.Count(f => f.Seat.RestWeekday != d));

                foreach (var c in crew.Where(c => c.Seat.RestWeekday is null))
                {
                    var crewmateDays = crew
                        .Where(o => o != c && o.Seat.RestWeekday is not null && Ci.Equals(o.Seat.VehicleId, c.Seat.VehicleId))
                        .Select(o => o.Seat.RestWeekday!.Value)
                        .ToHashSet();

                    var day = Weekdays
                        .OrderByDescending(d => covering[d] - crewResting[d])
                        .ThenBy(d => crewmateDays.Contains(d) ? 1 : 0)
                        .ThenBy(d => d)
                        .First();

                    c.Seat = c.Seat with { RestWeekday = day };
                    c.Said.Add($"{RestDayMark}{DayName(day)}, the day with the most floater cover to spare.");
                    crewResting[day]++;
                    justSet.Add(c);
                    restSet++;
                }

                var newRows = floaters.Where(f => f.NewFloater).ToList();
                if (newRows.Count > 0)
                {
                    var crewOnShift = Shifts.ToDictionary(sh => sh,
                        sh => places.Count(p => p.Seat.Kind == Crew && p.Seat.RouteId == routeId && Ci.Equals(p.Seat.Shift, sh)));
                    var quota = HomeShifts(floaters.Count, crewOnShift, crewOnShift.Values.Sum())
                        .GroupBy(sh => sh).ToDictionary(g => g.Key, g => g.Count());
                    var current = Shifts.ToDictionary(sh => sh,
                        sh => floaters.Count(f => !f.NewFloater && Ci.Equals(f.Seat.Shift, sh)));

                    foreach (var f in newRows)
                    {
                        var shift = Shifts
                            .OrderByDescending(sh => quota.GetValueOrDefault(sh) - current[sh])
                            .ThenBy(ShiftOrder)
                            .First();

                        f.Seat = f.Seat with { Shift = shift };
                        f.Said.Add($"Usually covers the {shift} shift, where the route is shortest of floaters.");
                        current[shift]++;
                    }
                }
            }

            // ---- 5. New rest days settled against the planner ---------------------------------
            //
            // Chosen above by counting floaters, which cannot see that a floater covering an
            // evening is not free the next morning. Only the rest days set just now may move:
            // they are nobody's yet, while a rest day a driver already has is theirs.

            foreach (var routeId in routesOnRoster.Where(id => justSet.Any(p => p.Seat.RouteId == id)))
            {
                var onRoute = places.Where(p => p.Seat.RouteId == routeId).ToList();
                var movable = Enumerable.Range(0, onRoute.Count).Where(k => justSet.Contains(onRoute[k])).ToHashSet();

                var settled = SettleNewRestDays(onRoute.Select(p => p.Seat).ToList(), movable, vehicles, drivers, routeNames);
                foreach (var (k, day) in settled)
                {
                    var p = onRoute[k];
                    p.Seat = p.Seat with { RestWeekday = day };
                    var at = p.Said.FindIndex(s => s.StartsWith(RestDayMark, StringComparison.Ordinal));
                    var said = $"{RestDayMark}{DayName(day)}, a day the {Route(routeId)} floaters can cover.";
                    if (at >= 0) p.Said[at] = said;
                    else p.Said.Add(said);
                }
            }

            if (restSet > 0)
                notes.Add(restSet == 1 ? "Set 1 rest day where it was missing." : $"Set {restSet} rest days where they were missing.");

            if (notes.Count == 0)
                notes.Add("Nothing to change: every place has an active driver and a rest day, and each route has the floaters it needs.");

            var result = places
                .OrderBy(p => p.Order)
                .Select(p => p.Said.Count > 0 ? p.Seat with { Suggested = string.Join(" ", p.Said) } : p.Seat)
                .ToList();

            return new AutoFillResult(result, notes, unmarked, filled, emptied, restSet, leftEmpty);
        }

        /// <summary>How a mark that sets a rest day begins.</summary>
        private const string RestDayMark = "Rests ";

        /// <summary>
        /// Whether a place's auto-fill mark is about who is in the place, rather than only its
        /// rest day.
        /// </summary>
        /// <remarks>
        /// A place whose driver a person chose is only ever given a rest day, so a mark that is
        /// nothing but the rest day leaves the driver as the person chose them. Any other mark
        /// begins with what auto-fill did about the driver: chose one, found nobody, or took one
        /// off. Read from the mark itself, so a mark saved with the roster answers the same.
        /// </remarks>
        public static bool MarkIsAboutDriver(string? suggested) =>
            !string.IsNullOrEmpty(suggested) && !suggested.StartsWith(RestDayMark, StringComparison.Ordinal);

        private static bool IsActiveDriver(UserModel d) =>
            d.RoleId == DriverRoleId && Ci.Equals(d.AccountStatus, "Activated");

        private static string Plural(int n, string one, string many) => n == 1 ? one : many;

        private static string Joined(IReadOnlyList<string> items) => items.Count switch
        {
            0 => "",
            1 => items[0],
            2 => $"{items[0]} and {items[1]}",
            _ => string.Join(", ", items.Take(items.Count - 1)) + $" and {items[^1]}",
        };

        /// <summary>A place on the roster while auto-fill works on it.</summary>
        private sealed class Place
        {
            public Place(int order, RosterSeat seat)
            {
                Order = order;
                Seat = seat;
            }

            /// <summary>Where it sits on the page; rows auto-fill adds come after every other.</summary>
            public int Order { get; }

            public RosterSeat Seat { get; set; }

            /// <summary>A floater row auto-fill added, dropped again if nobody can take it.</summary>
            public bool NewFloater { get; init; }

            /// <summary>What auto-fill did to this place, in the order it did it.</summary>
            public List<string> Said { get; } = new();
        }
    }
}
