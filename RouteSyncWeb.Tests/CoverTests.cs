using FleetWise.Models;
using FleetWise.Services;
using static RouteSyncWeb.Tests.World;

namespace RouteSyncWeb.Tests;

/// <summary>Covering the shifts that stand in the way of granting leave.</summary>
public class CoverTests
{
    private static readonly DateTime Tuesday = new(2026, 9, 15);

    private static LeaveRequest Pending(UserModel driver, DateTime from, DateTime to) => new()
    {
        RequestId = 900,
        UserId = driver.UserId,
        LeaveType = "Vacation",
        StartDate = from,
        EndDate = to,
        Status = "Pending",
    };

    [Fact]
    public void Each_suggestion_is_ranked_as_though_the_ones_before_it_were_saved()
    {
        var w = new World();
        var away = w.Driver("Absent");
        var tuesdayEvening = w.Trip(Tuesday, "Evening", away, w.Bus("B01"));
        var wednesdayMorning = w.Trip(Wednesday, "Morning", away, w.Bus("B02"));

        var spare = w.Bus("B99");
        var ana = w.Driver("Ana");
        for (var d = 1; d <= 3; d++)
            w.Trip(new DateTime(2026, 9, d), "Morning", ana, spare, status: "Completed");
        var bea = w.Driver("Bea");

        var s = SchedulingRules.AsIfGranted(w.Snapshot(), Pending(away, Tuesday, Wednesday));

        // Alone, Ana knows the route and tops both shifts.
        Assert.Equal(ana.UserId, SchedulingRules.RankDrivers(wednesdayMorning, s).Candidates[0].DriverId);

        var covers = SchedulingRules.SuggestCovers(new[] { wednesdayMorning, tuesdayEvening }, s);

        Assert.Equal(new[] { tuesdayEvening.TripId, wednesdayMorning.TripId }, covers.Select(c => c.Trip.TripId));
        Assert.Equal(ana.UserId, covers[0].Suggested?.DriverId);

        // Given the Tuesday Evening, a Wednesday Morning would break the rest rule.
        Assert.Equal(bea.UserId, covers[1].Suggested?.DriverId);
        Assert.DoesNotContain(covers[1].Candidates, c => c.DriverId == ana.UserId);

        // Suggesting never changes the snapshot it was asked about.
        Assert.All(s.Trips.Where(t => t.Date >= Tuesday), t => Assert.Equal(away.UserId, t.DriverId));
    }

    [Fact]
    public void A_cover_with_a_cost_is_refused_and_a_free_one_is_not()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Absent"), w.Bus("B01"));
        var spare = w.Bus("B99");

        var free = w.Driver("Ana");

        var carlo = w.Driver("Carlo");
        for (var d = 10; d <= 15; d++)
            w.Trip(new DateTime(2026, 9, d), "Morning", carlo, spare,
                   status: d < 14 ? "Completed" : "Not Yet Started");

        var dario = w.Driver("Dario");
        w.Trip(Wednesday, "Afternoon", dario, spare);

        var booked = w.Driver("Booked");
        w.Trip(Wednesday, "Morning", booked, w.Bus("B02"));

        var s = w.Snapshot();

        Assert.Null(SchedulingRules.CoverRefusal(trip, free.UserId, s));
        Assert.Contains("Would be 7 days in a row", SchedulingRules.CoverRefusal(trip, carlo.UserId, s));
        Assert.Contains("Runs straight into their Afternoon shift", SchedulingRules.CoverRefusal(trip, dario.UserId, s));
        Assert.Equal("Booked Cruz is not free for the Morning shift on Sep 16.",
                     SchedulingRules.CoverRefusal(trip, booked.UserId, s));
    }

    [Fact]
    public void A_shift_only_coverable_at_a_cost_gets_no_suggestion()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Absent"), w.Bus("B01"));
        w.Trip(Wednesday, "Afternoon", w.Driver("Dario"), w.Bus("B99"));

        var cover = Assert.Single(SchedulingRules.SuggestCovers(new[] { trip }, w.Snapshot()));

        Assert.Empty(cover.Candidates);
        Assert.Null(cover.Suggested);
    }

    [Fact]
    public void Leave_about_to_be_granted_counts_as_leave_and_leaves_the_rest_alone()
    {
        var w = new World();
        var away = w.Driver("Absent");
        var trip = w.Trip(Wednesday, "Morning", away, w.Bus("B01"));
        var other = w.Driver("Other");
        w.OnLeave(other, Wednesday, Wednesday);

        var plain = w.Snapshot();
        var request = Pending(away, Wednesday, Wednesday);
        var granted = SchedulingRules.AsIfGranted(plain, request);

        Assert.Empty(SchedulingRules.IssuesOf(trip, plain));
        Assert.Equal(new[] { AssignmentIssue.DriverOnLeave }, SchedulingRules.IssuesOf(trip, granted));

        Assert.Equal(2, granted.Leave.Count);
        Assert.Single(plain.Leave);
        Assert.Equal("Pending", request.Status);
    }
}
