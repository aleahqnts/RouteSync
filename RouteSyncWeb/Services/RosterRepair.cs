using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>What Suggest rest days proposes for a roster, and what it did, in sentences.</summary>
    /// <param name="Moved">Rest days a driver already had that were moved.</param>
    /// <param name="Added">Spare drivers added as floaters.</param>
    /// <param name="RestDaysSet">Rest days set where none was chosen yet.</param>
    /// <param name="GapsLeft">Crew rest days a week still uncovered once everything was tried.</param>
    /// <param name="Unmarked">The notes no place carries a mark for: a route that needs another floater.</param>
    public sealed record RestDaySuggestion(
        IReadOnlyList<RosterSeat> Seats, IReadOnlyList<string> Notes, IReadOnlyList<string> Unmarked,
        int Moved, int Added, int RestDaysSet, int GapsLeft)
    {
        public bool Changed => Moved + Added + RestDaysSet > 0;
    }

    public static partial class RosterRules
    {
        /// <summary>
        /// The roster with its rest days arranged so the floaters can cover them, disturbing as
        /// few people as possible. Nothing is saved; the page shows the result for a person to
        /// keep or change.
        /// </summary>
        /// <remarks>
        /// <para>A rest day is something a driver builds a week around, so the rest days people
        /// already have are where this starts, never a fresh arrangement. A roster that works
        /// is handed back as it came.</para>
        ///
        /// <para>Per route, each step taken only when the one before it cannot close the
        /// gaps the planner finds:</para>
        /// <list type="number">
        /// <item>Rest days missing are set, and moved if that helps. They were nobody's yet,
        /// so moving them disturbs nobody.</item>
        /// <item>A spare driver, active, unplaced and not held back, is added as a floater.
        /// Nobody's rest day moves.</item>
        /// <item>The fewest rest days are moved, each to the weekday that uncovers the least,
        /// and to a day near the one it was on when two are equal.</item>
        /// <item>Both together.</item>
        /// </list>
        /// <para>When nothing closes the gaps, the arrangement that leaves fewest is handed back
        /// and the route is said to need another floater, rather than a pattern that still
        /// collides being passed off as a fix.</para>
        ///
        /// <para>Floaters' usual shifts are left as they are. Only a floater added here is given
        /// one, the shift the route is shortest on.</para>
        /// </remarks>
        public static RestDaySuggestion SuggestRestDays(
            IReadOnlyList<RosterSeat> seats,
            IReadOnlyList<Vehicle> vehicles,
            IReadOnlyList<UserModel> drivers,
            IReadOnlyDictionary<int, string> routeNames,
            IReadOnlySet<int> held)
        {
            var driverById = new Dictionary<int, UserModel>();
            foreach (var d in drivers) driverById.TryAdd(d.UserId, d);

            var shared = drivers.GroupBy(NameOf, Ci).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(Ci);
            string Label(UserModel d) => shared.Contains(NameOf(d)) ? $"{NameOf(d)} ({d.UserId})" : NameOf(d);
            string Name(int id) => driverById.TryGetValue(id, out var d) ? Label(d) : $"Driver {id}";
            string Route(int id) => routeNames.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"route {id}";

            var result = seats.ToList();
            var marks = new Dictionary<int, string>();
            var notes = new List<string>();
            var unmarked = new List<string>();
            int moved = 0, added = 0, set = 0, gapsLeft = 0;

            var routes = result.Select(s => s.RouteId).Distinct().OrderBy(id => id).ToList();

            // Rest days nobody has chosen yet, set the way auto-fill sets them.
            var fresh = new HashSet<int>();
            foreach (var routeId in routes)
                foreach (var i in SetMissingRestDays(result, routeId))
                    fresh.Add(i);

            var placed = result.Where(s => s.DriverId is not null).Select(s => s.DriverId!.Value).ToHashSet();
            var spares = drivers
                .Where(d => IsActiveDriver(d) && !placed.Contains(d.UserId) && !held.Contains(d.UserId))
                .OrderBy(NameOf, Ci)
                .ThenBy(d => d.UserId)
                .ToList();

            var newRows = new List<RosterSeat>();

            foreach (var routeId in routes)
            {
                var idx = Enumerable.Range(0, result.Count).Where(i => result[i].RouteId == routeId).ToList();
                var local = idx.Select(i => result[i]).ToList();
                var before = idx.Select(i => seats[i].RestWeekday).ToList();
                var freshLocal = Enumerable.Range(0, idx.Count).Where(k => fresh.Contains(idx[k])).ToHashSet();

                int Count(List<RosterSeat> s) => RosterStructure.RestDayGapCount(s, drivers, vehicles, routeNames);

                var gaps = Count(local);
                var chosen = new Attempt(local, gaps, 0, null);

                if (gaps > 0)
                {
                    var attempts = new List<Attempt>();

                    var free = MoveRestDays(local, gaps, k => freshLocal.Contains(k), before, Count);
                    attempts.Add(new Attempt(free.Seats, free.Gaps, 0, null));

                    if (free.Gaps > 0)
                    {
                        var spare = spares.Count > 0 ? WithSpare(free.Seats, spares[0], routeId, drivers, vehicles, routeNames, Count) : null;
                        if (spare is not null) attempts.Add(spare);

                        if (spare is null || spare.Gaps > 0)
                        {
                            var moves = MoveRestDays(free.Seats, free.Gaps, _ => true, before, Count);
                            attempts.Add(new Attempt(moves.Seats, moves.Gaps, Moved(moves.Seats, before, freshLocal), null));

                            if (moves.Gaps > 0 && spare is not null)
                            {
                                var withSpareBefore = before.Append(spare.Seats[^1].RestWeekday).ToList();
                                var both = MoveRestDays(spare.Seats, spare.Gaps, _ => true, withSpareBefore, Count);
                                attempts.Add(new Attempt(both.Seats, both.Gaps, Moved(both.Seats, before, freshLocal), spare.Spare));
                            }
                        }
                    }

                    // Fewest gaps; on a tie the least disruption, in the order the steps were tried.
                    chosen = attempts
                        .Select((a, order) => (a, order))
                        .OrderBy(x => x.a.Gaps)
                        .ThenBy(x => x.order)
                        .First().a;
                }

                // Written back: existing places by position, an added floater after them.
                for (var k = 0; k < idx.Count; k++)
                {
                    var was = before[k];
                    var now = chosen.Seats[k].RestWeekday;
                    result[idx[k]] = chosen.Seats[k];

                    if (freshLocal.Contains(k))
                    {
                        marks[idx[k]] = $"{RestDayMark}{DayName(now!.Value)}, where it was missing.";
                        set++;
                    }
                    else if (was is int from && now is int to && from != to)
                    {
                        marks[idx[k]] = $"{RestDayMark}{DayName(to)}, moved from {DayName(from)} so the route's floaters can cover every rest day.";
                        notes.Add($"Moved {Name(chosen.Seats[k].DriverId!.Value)}'s rest day from {DayName(from)} to {DayName(to)}.");
                        moved++;
                    }
                }

                if (chosen.Spare is UserModel taken)
                {
                    var row = chosen.Seats[^1];
                    var movedHere = chosen.Moves > 0;
                    newRows.Add(row with
                    {
                        Suggested = $"Added as a {Route(routeId)} floater so its rest days are covered."
                            + (movedHere ? "" : " Nobody's rest day moved."),
                    });
                    notes.Add($"Added {Label(taken)} as a {Route(routeId)} floater, resting {DayName(row.RestWeekday!.Value)}.");
                    spares.Remove(taken);
                    added++;
                }

                if (chosen.Gaps > 0)
                {
                    var weekly = RosterStructure.WeeklyGaps(chosen.Seats, drivers, vehicles, routeNames)
                        .Count(g => g.RouteId == routeId && g.RestDay);
                    gapsLeft += weekly;
                    var note = $"{Route(routeId)} still leaves {weekly} bus {(weekly == 1 ? "shift" : "shifts")} a week "
                        + $"with nobody to cover {(weekly == 1 ? "it" : "them")}, however its rest days are arranged. It needs another floater.";
                    notes.Add(note);
                    unmarked.Add(note);
                }
            }

            if (set > 0)
                notes.Add(set == 1 ? "Set 1 rest day where it was missing." : $"Set {set} rest days where they were missing.");

            if (notes.Count == 0)
                notes.Add("Every rest day is covered as it stands. Nothing to change.");

            // A mark that was only about the rest day is replaced; one about who is in the place
            // keeps its sentence, with the rest day said after it.
            for (var i = 0; i < result.Count; i++)
            {
                if (!marks.TryGetValue(i, out var mark)) continue;
                var had = result[i].Suggested;
                result[i] = result[i] with
                {
                    Suggested = string.IsNullOrWhiteSpace(had) || had.StartsWith(RestDayMark, StringComparison.Ordinal)
                        ? mark
                        : $"{had} {mark}",
                };
            }

            result.AddRange(newRows);
            return new RestDaySuggestion(result, notes, unmarked, moved, added, set, gapsLeft);
        }

        /// <summary>
        /// Moves only the rest days auto-fill has just set, and only where that uncovers less,
        /// leaving every rest day a driver already had where it was.
        /// </summary>
        /// <returns>The new rest day for each place moved, by its position in <paramref name="routeSeats"/>.</returns>
        internal static Dictionary<int, int> SettleNewRestDays(
            IReadOnlyList<RosterSeat> routeSeats,
            IReadOnlySet<int> justSet,
            IReadOnlyList<Vehicle> vehicles,
            IReadOnlyList<UserModel> drivers,
            IReadOnlyDictionary<int, string> routeNames)
        {
            var local = routeSeats.ToList();
            int Count(List<RosterSeat> s) => RosterStructure.RestDayGapCount(s, drivers, vehicles, routeNames);

            var gaps = Count(local);
            if (gaps == 0 || justSet.Count == 0) return new();

            var before = local.Select(s => s.RestWeekday).ToList();
            var settled = MoveRestDays(local, gaps, justSet.Contains, before, Count);

            var changed = new Dictionary<int, int>();
            for (var k = 0; k < local.Count; k++)
                if (settled.Seats[k].RestWeekday is int to && to != before[k])
                    changed[k] = to;
            return changed;
        }

        /// <summary>One way of arranging a route, and what it costs.</summary>
        /// <param name="Moves">Rest days a driver already had that it moves.</param>
        /// <param name="Spare">The spare driver it adds as a floater, last in <paramref name="Seats"/>, or null.</param>
        private sealed record Attempt(List<RosterSeat> Seats, int Gaps, int Moves, UserModel? Spare);

        /// <summary>
        /// Moves one rest day at a time, each time the one that uncovers the least, until
        /// nothing is left uncovered or no single move helps.
        /// </summary>
        /// <remarks>
        /// Each move must leave strictly fewer gaps, so this ends. Between two moves that leave
        /// as many, a crew driver's is moved before a floater's, then the one that moves the
        /// day least from where it was, so a Wednesday becomes a Thursday before a Sunday, then
        /// the day fewest crew already rest on, so rest days spread rather than stack and a
        /// floater is not handed two shifts on one day when a quieter day was as near.
        /// </remarks>
        private static (List<RosterSeat> Seats, int Gaps) MoveRestDays(
            List<RosterSeat> start, int gaps, Func<int, bool> movable, IReadOnlyList<int?> before,
            Func<List<RosterSeat>, int> count)
        {
            var current = start.ToList();

            while (gaps > 0)
            {
                (int At, int Day, int Gaps, int Rank)? best = null;

                for (var k = 0; k < current.Count; k++)
                {
                    if (!movable(k) || current[k].DriverId is null || current[k].RestWeekday is not int now) continue;

                    foreach (var day in Weekdays)
                    {
                        if (day == now) continue;

                        var trial = current.ToList();
                        trial[k] = trial[k] with { RestWeekday = day };
                        var left = count(trial);
                        if (left >= gaps) continue;

                        var from = k < before.Count ? before[k] : null;
                        var resting = 0;
                        for (var j = 0; j < current.Count; j++)
                            if (j != k && current[j].Kind == Crew && current[j].RestWeekday == day) resting++;
                        var rank = (current[k].Kind == Crew ? 0 : 1000) + Distance(from ?? now, day) * 10 + resting;

                        if (best is null
                            || left < best.Value.Gaps
                            || (left == best.Value.Gaps && rank < best.Value.Rank))
                            best = (k, day, left, rank);
                    }
                }

                if (best is null) break;

                current[best.Value.At] = current[best.Value.At] with { RestWeekday = best.Value.Day };
                gaps = best.Value.Gaps;
            }

            return (current, gaps);
        }

        /// <summary>A spare driver added as a floater on the rest day that uncovers least.</summary>
        private static Attempt WithSpare(
            List<RosterSeat> start, UserModel spare, int routeId,
            IReadOnlyList<UserModel> drivers, IReadOnlyList<Vehicle> vehicles,
            IReadOnlyDictionary<int, string> routeNames, Func<List<RosterSeat>, int> count)
        {
            // Usually covers the shift the route leaves empty most, so they are the one picked for it.
            var shortOn = RosterStructure.CleanPlan(start, drivers, vehicles, routeNames).Gaps
                .GroupBy(g => g.Shift, Ci)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => ShiftOrder(g.Key))
                .Select(g => Shifts.First(sh => Ci.Equals(sh, g.Key)))
                .FirstOrDefault() ?? Shifts[0];

            var floatersResting = Weekdays.ToDictionary(d => d, d => start.Count(s => s.Kind == Floater && s.RestWeekday == d));
            var crewResting = Weekdays.ToDictionary(d => d, d => start.Count(s => s.Kind == Crew && s.RestWeekday == d));

            return Weekdays
                .Select(day =>
                {
                    var trial = start.ToList();
                    trial.Add(new RosterSeat(spare.UserId, Floater, routeId, null, shortOn, day));
                    return (trial, day, gaps: count(trial));
                })
                .OrderBy(x => x.gaps)
                .ThenBy(x => floatersResting[x.day])
                .ThenBy(x => crewResting[x.day])
                .ThenBy(x => x.day)
                .Select(x => new Attempt(x.trial, x.gaps, 0, spare))
                .First();
        }

        /// <summary>Rest days a driver already had that an arrangement moves.</summary>
        private static int Moved(List<RosterSeat> seats, IReadOnlyList<int?> before, IReadOnlySet<int> justSet) =>
            Enumerable.Range(0, Math.Min(seats.Count, before.Count))
                .Count(k => !justSet.Contains(k) && before[k] is int from && seats[k].RestWeekday is int to && from != to);

        /// <summary>Days apart around the week, so Sunday and Monday are neighbours.</summary>
        private static int Distance(int from, int to)
        {
            var d = Math.Abs(from - to);
            return Math.Min(d, 7 - d);
        }

        /// <summary>
        /// Sets missing rest days on one route the way auto-fill does: a floater on the day
        /// fewest floaters rest, a crew driver on the day with the most floater cover to spare,
        /// away from a crewmate's day.
        /// </summary>
        /// <returns>The positions given a rest day.</returns>
        private static List<int> SetMissingRestDays(List<RosterSeat> seats, int routeId)
        {
            var set = new List<int>();

            var floaters = Enumerable.Range(0, seats.Count)
                .Where(i => seats[i].RouteId == routeId && seats[i].Kind == Floater && seats[i].DriverId is not null)
                .ToList();
            var crew = Enumerable.Range(0, seats.Count)
                .Where(i => seats[i].RouteId == routeId && seats[i].Kind == Crew && seats[i].DriverId is not null)
                .OrderBy(i => seats[i].VehicleId, StringComparer.Ordinal)
                .ThenBy(i => ShiftOrder(seats[i].Shift))
                .ToList();

            var floatersResting = Weekdays.ToDictionary(d => d, d => floaters.Count(i => seats[i].RestWeekday == d));
            var crewResting = Weekdays.ToDictionary(d => d, d => crew.Count(i => seats[i].RestWeekday == d));

            foreach (var i in floaters.Where(i => seats[i].RestWeekday is null))
            {
                var day = Weekdays.OrderBy(d => floatersResting[d]).ThenBy(d => crewResting[d]).ThenBy(d => d).First();
                seats[i] = seats[i] with { RestWeekday = day };
                floatersResting[day]++;
                set.Add(i);
            }

            var covering = Weekdays.ToDictionary(d => d, d => floaters.Count(i => seats[i].RestWeekday != d));

            foreach (var i in crew.Where(i => seats[i].RestWeekday is null))
            {
                var crewmateDays = crew
                    .Where(o => o != i && seats[o].RestWeekday is not null && Ci.Equals(seats[o].VehicleId, seats[i].VehicleId))
                    .Select(o => seats[o].RestWeekday!.Value)
                    .ToHashSet();

                var day = Weekdays
                    .OrderByDescending(d => covering[d] - crewResting[d])
                    .ThenBy(d => crewmateDays.Contains(d) ? 1 : 0)
                    .ThenBy(d => d)
                    .First();

                seats[i] = seats[i] with { RestWeekday = day };
                crewResting[day]++;
                set.Add(i);
            }

            return set;
        }
    }
}
