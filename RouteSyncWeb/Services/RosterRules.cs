using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>One place a driver holds in a month's roster, or a bus shift still waiting for one.</summary>
    /// <param name="DriverId">Null for a shift the bus runs that has nobody on it yet.</param>
    /// <param name="Kind"><see cref="RosterRules.Crew"/> or <see cref="RosterRules.Floater"/>.</param>
    /// <param name="VehicleId">The bus, for crew. Null for a floater.</param>
    /// <param name="Shift">Crew: the shift driven. Floater: the home shift, preferred and not a lock.</param>
    /// <param name="RestWeekday">ISO weekday, 1 Monday to 7 Sunday, or null while unset.</param>
    /// <param name="Suggested">Why auto-fill filled or emptied this place, or null for a place a person set.</param>
    public sealed record RosterSeat(
        int? DriverId, string Kind, int RouteId, string? VehicleId, string Shift, int? RestWeekday,
        string? Suggested = null);

    /// <summary>How one weekday stands on a route.</summary>
    /// <param name="Resting">Crew drivers resting that day.</param>
    /// <param name="Covering">Floaters not resting that day, which is not the same as free.</param>
    /// <param name="Short">
    /// A crew rest day that day which no floater can cover, every week, as the planner finds it.
    /// Not <c>Resting &gt; Covering</c>: a floater working the evening before is not free for a
    /// morning, whatever the count says.
    /// </param>
    public sealed record DayCover(int Weekday, int Resting, int Covering, bool Short);

    /// <summary>What a route is short of, stated rather than hidden.</summary>
    /// <param name="CrewSeats">Bus shifts the route runs, filled or not.</param>
    /// <param name="UnfilledSeats">Bus shifts the route runs with nobody on them.</param>
    /// <param name="FloatersNeeded">
    /// The usual minimum: one floater for every six bus shifts, since each crew driver rests one
    /// day in seven. A guide for staffing a route, not a promise the rest days are covered.
    /// </param>
    /// <param name="GapsPerWeek">Crew rest days each week that no floater can cover, as the planner finds them.</param>
    public sealed record RouteShortfall(
        int RouteId, int CrewSeats, int UnfilledSeats, int Floaters, int FloatersNeeded, int GapsPerWeek)
    {
        /// <summary>Floaters the route needs beyond those it has, by the usual minimum.</summary>
        public int FloatersMissing => Math.Max(0, FloatersNeeded - Floaters);

        /// <summary>Drivers the route needs beyond those placed on it.</summary>
        public int DriversShort => UnfilledSeats + FloatersMissing;

        /// <summary>
        /// Enough floaters by the usual minimum, and rest days left uncovered anyway: the rest
        /// days are arranged so one floater is needed in two places at once. Moving a rest day
        /// fixes it; another floater only hides it.
        /// </summary>
        public bool RestDaysCollide => FloatersMissing == 0 && Floaters > 0 && GapsPerWeek > 0;
    }

    /// <summary>
    /// The rules for building a month's roster: what makes one savable, how its rest days
    /// cover, and where rest days are best put.
    /// </summary>
    /// <remarks>
    /// <para>Every function is pure. The Roster page sends the roster as it stands on screen
    /// and gets back what is wrong with it and how each route covers, so the page and the
    /// save are judged by the same rules.</para>
    ///
    /// <para>A crew driver works six days and rests on a fixed weekday. Floaters belong to a
    /// route and cover its crew's rest days, one a day each, so a route usually needs a
    /// floater for every six crew seats, rounded up. Whether a given arrangement of rest days
    /// is actually covered is the planner's to say, through <see cref="RosterStructure"/>.</para>
    /// </remarks>
    public static partial class RosterRules
    {
        public const string Crew = "Crew";
        public const string Floater = "Floater";

        /// <summary>Shifts in the order a day runs them.</summary>
        public static readonly IReadOnlyList<string> Shifts = new[] { "Morning", "Afternoon", "Evening" };

        private static readonly string[] DayNames = { "", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

        private const int DriverRoleId = 2;

        /// <summary>Monday to Sunday, as ISO weekdays.</summary>
        public static IEnumerable<int> Weekdays => Enumerable.Range(1, 7);

        /// <summary>A weekday's full name.</summary>
        public static string DayName(int weekday) => weekday is >= 1 and <= 7 ? DayNames[weekday] : "";

        /// <summary>Why a roster cannot be saved as it stands. Empty when it can.</summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item>A driver sits in one place a month: one crew seat or one floater row.</item>
        /// <item>Every crew driver and floater has a rest day.</item>
        /// <item>A shift the bus runs has one driver at most. It may have none yet: a publish
        /// covers such a place from the route's floaters, or leaves it unfilled with a reason,
        /// and the route's shortfall note counts it.</item>
        /// <item>A retired bus holds no crew, and a crew sits on the route its bus is based on.</item>
        /// <item>Only an active driver account is placed.</item>
        /// </list>
        /// </remarks>
        public static IReadOnlyList<string> Problems(
            IReadOnlyList<RosterSeat> seats,
            IReadOnlyList<Vehicle> vehicles,
            IReadOnlyList<UserModel> drivers,
            IReadOnlyDictionary<int, string> routeNames)
        {
            var problems = new List<string>();
            var busById = vehicles.ToDictionary(v => v.VehicleId, Ci);
            var driverById = drivers.ToDictionary(d => d.UserId);

            string Route(int id) => routeNames.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"route {id}";
            string Name(int id) => driverById.TryGetValue(id, out var d) ? NameOf(d) : $"Driver {id}";
            string Place(RosterSeat s) => s.Kind == Crew
                ? $"{s.VehicleId} on the {s.Shift} shift"
                : $"a {Route(s.RouteId)} floater";

            foreach (var s in seats)
            {
                if (s.Kind != Crew && s.Kind != Floater)
                {
                    problems.Add("A place on the roster is neither crew nor floater.");
                    continue;
                }

                if (!Shifts.Contains(s.Shift))
                {
                    problems.Add($"{s.Shift} is not a shift.");
                    continue;
                }

                if (!routeNames.ContainsKey(s.RouteId))
                    problems.Add($"Route {s.RouteId} does not exist.");

                if (s.Kind == Crew)
                {
                    if (string.IsNullOrEmpty(s.VehicleId) || !busById.TryGetValue(s.VehicleId, out var bus))
                    {
                        problems.Add($"Bus {s.VehicleId} does not exist.");
                    }
                    else
                    {
                        if (bus.RetiredAt != null)
                            problems.Add($"{bus.VehicleId} is retired and cannot hold a crew.");
                        else if (bus.RouteId != s.RouteId)
                            problems.Add($"{bus.VehicleId} is not based on {Route(s.RouteId)}. Move its crew to the route it runs.");
                    }
                }
                else if (s.DriverId is null)
                {
                    problems.Add($"A {Route(s.RouteId)} floater row has no driver.");
                }

                if (s.DriverId is int id)
                {
                    if (!driverById.TryGetValue(id, out var driver) || driver.RoleId != DriverRoleId)
                        problems.Add($"Driver {id} is not a driver account.");
                    else if (!Ci.Equals(driver.AccountStatus, "Activated"))
                        problems.Add($"{NameOf(driver)} is not an active driver.");

                    if (s.RestWeekday is null)
                        problems.Add($"{Name(id)} has no rest day.");
                    else if (s.RestWeekday is < 1 or > 7)
                        problems.Add($"{Name(id)} has a rest day that is not a day of the week.");
                }
            }

            foreach (var twice in seats.Where(s => s.Kind == Crew && !string.IsNullOrEmpty(s.VehicleId))
                                       .GroupBy(s => (Bus: s.VehicleId!.ToUpperInvariant(), s.Shift))
                                       .Where(g => g.Count() > 1))
                problems.Add($"{twice.First().VehicleId} has more than one driver on the {twice.Key.Shift} shift.");

            foreach (var twice in seats.Where(s => s.DriverId is not null)
                                       .GroupBy(s => s.DriverId!.Value)
                                       .Where(g => g.Count() > 1))
                problems.Add($"{Name(twice.Key)} is placed more than once: {string.Join(" and ", twice.Select(Place))}.");

            return problems.Distinct().ToList();
        }

        /// <summary>
        /// For each weekday, how many of a route's crew rest and how many of its floaters are
        /// working, and whether a rest day that day goes uncovered.
        /// </summary>
        /// <remarks>
        /// The two counts are for reading at a glance. Whether the day is short comes from the
        /// planner's weekly gaps, which is the only thing that knows a floater is not free the
        /// morning after an evening. Crew seats with no driver or no rest day yet count for
        /// nothing, so the strip shows the roster as far as it has been built.
        /// </remarks>
        public static IReadOnlyList<DayCover> Capacity(
            int routeId, IReadOnlyList<RosterSeat> seats, IReadOnlyList<WeeklyGap> weekly)
        {
            var onRoute = seats.Where(s => s.RouteId == routeId && s.DriverId is not null && s.RestWeekday is not null).ToList();
            var crew = onRoute.Where(s => s.Kind == Crew).ToList();
            var floaters = onRoute.Where(s => s.Kind == Floater).ToList();

            return Weekdays
                .Select(d => new DayCover(d,
                    crew.Count(s => s.RestWeekday == d),
                    floaters.Count(s => s.RestWeekday != d),
                    weekly.Any(g => g.RouteId == routeId && g.Weekday == d && g.RestDay)))
                .ToList();
        }

        /// <summary>What a route needs that it does not have.</summary>
        /// <remarks>
        /// Drivers short counts the route's bus shifts with nobody on them, and the floaters it
        /// needs beyond those it has by the usual minimum. Gaps per week counts the crew rest
        /// days the planner cannot cover, which is what the roster will leave unfilled every
        /// week whatever the minimum says.
        /// </remarks>
        public static RouteShortfall Shortfall(
            int routeId, IReadOnlyList<RosterSeat> seats, IReadOnlyList<WeeklyGap> weekly)
        {
            var crewSeats = seats.Where(s => s.RouteId == routeId && s.Kind == Crew).ToList();
            var floaters = seats.Count(s => s.RouteId == routeId && s.Kind == Floater && s.DriverId is not null);
            var needed = (int)Math.Ceiling(crewSeats.Count / 6.0);
            var unfilled = crewSeats.Count(s => s.DriverId is null);
            var gaps = weekly.Count(g => g.RouteId == routeId && g.RestDay);

            return new RouteShortfall(routeId, crewSeats.Count, unfilled, floaters, needed, gaps);
        }

        /// <summary>Active drivers holding no place on the roster.</summary>
        public static IReadOnlyList<UserModel> Unplaced(IReadOnlyList<RosterSeat> seats, IReadOnlyList<UserModel> drivers)
        {
            var placed = seats.Where(s => s.DriverId is not null).Select(s => s.DriverId!.Value).ToHashSet();
            return drivers
                .Where(d => d.RoleId == DriverRoleId && Ci.Equals(d.AccountStatus, "Activated") && !placed.Contains(d.UserId))
                .OrderBy(NameOf, Ci)
                .ThenBy(d => d.UserId)
                .ToList();
        }

        private static List<string> HomeShifts(int floaters, Dictionary<string, int> crewOnShift, int totalCrew)
        {
            if (totalCrew == 0)
                return Enumerable.Range(0, floaters).Select(k => Shifts[k % Shifts.Count]).ToList();

            var quotas = Shifts
                .Select((sh, order) => (sh, order, exact: floaters * (double)crewOnShift[sh] / totalCrew))
                .Select(x => (x.sh, x.order, whole: (int)Math.Floor(x.exact), rem: x.exact - Math.Floor(x.exact)))
                .ToList();

            var left = floaters - quotas.Sum(q => q.whole);
            var extra = quotas.OrderByDescending(q => q.rem).ThenBy(q => q.order).Take(left).Select(q => q.sh).ToHashSet();

            return quotas
                .OrderBy(q => q.order)
                .SelectMany(q => Enumerable.Repeat(q.sh, q.whole + (extra.Contains(q.sh) ? 1 : 0)))
                .ToList();
        }

        private static int ShiftOrder(string shift)
        {
            var i = Shifts.ToList().IndexOf(shift);
            return i < 0 ? Shifts.Count : i;
        }

        private static string NameOf(UserModel d)
        {
            var name = $"{d.FirstName} {d.LastName}".Trim();
            return name.Length == 0 ? $"Driver {d.UserId}" : name;
        }
    }
}
