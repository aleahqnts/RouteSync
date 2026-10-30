using System.Globalization;
using FleetWise.Models;
using FleetWise.Services;
using FleetWise.ViewModels;
using Microsoft.AspNetCore.Mvc;
using static Postgrest.Constants;

namespace FleetWise.Controllers
{
    // Carrying a roster forward, publishing it as trips, and what publishing leaves behind.
    //
    // The work itself is RosterPublisher's, shared with the monthly cycle that
    // drafts and publishes on schedule. These actions only turn its answers into responses.
    public partial class RosterController
    {
        private static readonly CultureInfo En = CultureInfo.InvariantCulture;

        // Fills this month's draft with last month's roster, every shift rotated one step backward, then auto-filled.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Generate([FromBody] RosterMonthInput req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (!TryParseMonth(req.Month, out var month)) return BadRequest("That is not a month.");
            if (month < FirstOf(PhClock.OperationalDay)) return BadRequest("That month is over, so its roster can no longer be changed.");

            return Answer(await _publisher.GenerateAsync(month, req.Version, SenderId()),
                r => Ok(new
                {
                    version = r.Version,
                    notes = r.Lines ?? Array.Empty<string>(),
                    unmarked = r.Unmarked ?? Array.Empty<string>(),
                }));
        }

        // What publishing the saved roster would write, without writing it.
        [HttpPost]
        public async Task<IActionResult> Preview([FromBody] RosterMonthInput req)
        {
            if (!ModelState.IsValid) return BadRequest(new { problems = new[] { ModelState.FirstError() } });
            if (!TryParseMonth(req.Month, out var month)) return BadRequest(new { problems = new[] { "That is not a month." } });

            return Answer(await _publisher.PreviewAsync(month, req.Version), r => Json(new { lines = r.Lines }));
        }

        // Publishes the saved roster as the month's trips.
        //
        // The plan is worked out again from the roster as saved, never taken from the page,
        // and publish_roster_month checks every row of it once more under a lock.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Publish([FromBody] RosterMonthInput req)
        {
            if (!ModelState.IsValid) return BadRequest(new { problems = new[] { ModelState.FirstError() } });
            if (!TryParseMonth(req.Month, out var month)) return BadRequest(new { problems = new[] { "That is not a month." } });

            return Answer(await _publisher.PublishAsync(month, req.Version, SenderId()),
                r => Ok(new { version = r.Version, lines = r.Lines }));
        }

        private IActionResult Answer(RosterStepResult r, Func<RosterStepResult, IActionResult> done) => r.Step switch
        {
            RosterStep.Done => done(r),
            RosterStep.Stale or RosterStep.Clash => Conflict(new { message = r.Message }),
            _ => BadRequest(new { problems = r.Problems ?? new[] { r.Message ?? "That could not be done." } }),
        };

        // Marks a gap as a bus not running that day, so no publish fills it.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SkipGap([FromBody] RosterSlotInput req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (!TryParseDay(req.Date, out var day)) return BadRequest("That is not a date.");

            var month = FirstOf(day);
            if (await _publisher.ReadMonthAsync(month) is null) return BadRequest($"There is no roster for {month:MMMM}.");

            try
            {
                await _supabase.From<RosterSkip>().Insert(new RosterSkip
                {
                    Date = day,
                    VehicleId = req.VehicleId,
                    Shift = req.Shift,
                    Month = month,
                    Reason = "Not running",
                    CreatedBy = SenderId()?.ToString(En),
                });
            }
            catch (Postgrest.Exceptions.PostgrestException ex) when (RosterPublisher.DatabaseCode(ex.Content) is "23505")
            {
                // Already skipped, which is what was asked for.
            }

            await _audit.WriteAsync("roster_skip_added",
                $"marked bus {req.VehicleId} as not running on the {req.Shift} shift of {day:MMM d, yyyy}",
                "roster_skips", $"{day:yyyy-MM-dd}|{req.VehicleId}|{req.Shift}");

            return Ok();
        }

        // Lets the next publish fill a skipped slot again.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RestoreSkip([FromBody] RosterSlotInput req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (!TryParseDay(req.Date, out var day)) return BadRequest("That is not a date.");

            await _supabase.From<RosterSkip>()
                .Filter("date", Operator.Equals, day.ToString("yyyy-MM-dd"))
                .Filter("vehicle_id", Operator.Equals, req.VehicleId)
                .Filter("shift", Operator.Equals, req.Shift)
                .Delete();

            await _audit.WriteAsync("roster_skip_restored",
                $"restored bus {req.VehicleId} on the {req.Shift} shift of {day:MMM d, yyyy}, so the next publish fills it",
                "roster_skips", $"{day:yyyy-MM-dd}|{req.VehicleId}|{req.Shift}");

            return Ok();
        }

        // Drivers who could fill a gap, ranked by the same rules as every replacement.
        [HttpGet]
        [RequirePermission("routes")]
        public async Task<IActionResult> GapCandidates(string date, string vehicleId, string shift)
        {
            if (!TryParseDay(date, out var day) || !TripStatus.Windows.ContainsKey(shift ?? ""))
                return BadRequest("That is not a slot on the roster.");

            var snapshot = await _scheduling.LoadAsync(day, day);
            var bus = snapshot.Vehicles.FirstOrDefault(v => string.Equals(v.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase));
            if (bus is null) return NotFound("That bus does not exist.");

            var ranking = SchedulingRules.RankDrivers(
                SchedulingRules.OpenSlot(day, shift!, bus.RouteId ?? 0, bus.VehicleId), snapshot);

            return Json(new
            {
                candidates = ranking.Candidates.Select(CandidateJson.Driver),
                shortfall = SchedulingRules.DriverShortfall(ranking),
            });
        }

        // Books a driver into a gap as a trip made by hand.
        //
        // Made by hand rather than by the roster, so a later publish builds around it
        // instead of rewriting a choice a person made. The gap reads as filled from then on.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission("routes")]
        public async Task<IActionResult> FillGap([FromBody] RosterFillInput req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState.FirstError());
            if (!TryParseDay(req.Date, out var day) || !TripStatus.Windows.TryGetValue(req.Shift, out var window))
                return BadRequest("That is not a slot on the roster.");

            var bus = (await _supabase.From<Vehicle>().Filter("vehicle_id", Operator.Equals, req.VehicleId).Get()).Models.FirstOrDefault();
            if (bus is null || bus.RouteId is null) return BadRequest("That bus has no route to run.");

            // Where the booked driver sat in the ranking for the gap, measured before the
            // booking, and never allowed to stand in its way.
            ReassignmentTag? tag = null;
            try
            {
                var snapshot = await _scheduling.LoadAsync(day, day);
                tag = SchedulingRules.TagFill(
                    SchedulingRules.OpenSlot(day, req.Shift, bus.RouteId.Value, bus.VehicleId),
                    req.DriverId, snapshot, PickScreen.Roster);
            }
            catch (Exception ex)
            {
                await _audit.WriteAsync("trip_created",
                    $"could not rank the gap on bus {bus.VehicleId} for the record: {ex.Message}",
                    "trips", outcome: "failed");
            }

            var senderId = SenderId() ?? 0;
            var result = await _assignments.CreateAsync(
                new NewTrip(day, req.Shift, window.Start, window.End, bus.RouteId.Value, bus.VehicleId, req.DriverId, req.Override),
                senderId, purpose: "filling a roster gap", tag: tag);

            if (result.Outcome == ReassignOutcome.Conflict) return Conflict(new { conflict = result.Message });
            if (result.Outcome != ReassignOutcome.Done) return BadRequest(result.Message);

            if (day > PhClock.OperationalDay && result.Trip is not null)
            {
                try { await _assignments.NotifyNewShiftAsync(result.Trip, senderId); }
                catch (Exception ex)
                {
                    await _audit.WriteAsync("trip_created",
                        $"could not tell driver {req.DriverId} about trip {result.Trip.TripId}: {ex.Message}",
                        "trips", result.Trip.TripId, outcome: "failed");
                }
            }

            return Ok(new { tripId = result.Trip?.TripId });
        }

        // The publish state, gaps and skips the Roster page shows under its header.
        // openGaps: The month's open gaps, already being read; null when there is no roster.
        private async Task FillPublishStateAsync(RosterViewModel vm, RosterMonth? roster, IReadOnlyList<BusRoute> routes,
            bool hasPreviousRoster, Task<List<RosterGap>>? openGaps, List<RosterSkip> skips)
        {
            vm.CanFillGaps = User.HasClaim("perm", "routes");
            vm.HasPreviousRoster = hasPreviousRoster;

            if (roster?.PublishedAt is DateTime published)
            {
                vm.PublishedLine = "Published " + PhClock.ToPh(new DateTimeOffset(published)).ToString("MMM d, h:mm tt", En);
                vm.ChangedSincePublish = roster.SavedAt is DateTime saved && saved > published;
            }

            if (roster is null || openGaps is null) return;

            var routeNames = routes.ToDictionary(r => r.RouteId, r => r.RouteName);
            var gaps = await openGaps;

            vm.Gaps = gaps
                .OrderBy(g => g.Date).ThenBy(g => ShiftIndex(g.Shift)).ThenBy(g => g.VehicleId, StringComparer.Ordinal)
                .Select(g => SlotView(g.Date, g.Shift, g.VehicleId, g.RouteId is int r ? routeNames.GetValueOrDefault(r, "") : "", g.Reason))
                .ToList();

            vm.Skips = skips
                .Where(s => s.Date.Date > PhClock.OperationalDay)
                .OrderBy(s => s.Date).ThenBy(s => ShiftIndex(s.Shift)).ThenBy(s => s.VehicleId, StringComparer.Ordinal)
                .Select(s => SlotView(s.Date, s.Shift, s.VehicleId, "",
                    s.Reason == "Deleted" ? "The roster trip was deleted" : "Marked as not running"))
                .ToList();
        }

        private static RosterSlotView SlotView(DateTime day, string shift, string bus, string route, string reason) => new()
        {
            Date = day.ToString("yyyy-MM-dd"),
            DateLabel = day.ToString("ddd, MMM d", En),
            Shift = shift,
            VehicleId = bus,
            RouteName = route,
            Reason = reason,
        };

        private static int ShiftIndex(string shift) => RosterRules.Shifts.ToList().IndexOf(shift);

        private static bool TryParseDay(string? value, out DateTime day) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", En, DateTimeStyles.None, out day);
    }
}
