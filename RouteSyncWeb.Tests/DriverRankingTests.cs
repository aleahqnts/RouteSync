using FleetWise.Services;
using static RouteSyncWeb.Tests.World;

namespace RouteSyncWeb.Tests;

public class DriverRankingTests
{
    /// <summary>A Wednesday Morning trip on North Loop whose driver needs replacing.</summary>
    private static (World W, FleetWise.Models.Trip Trip) Setup()
    {
        var w = new World();
        var away = w.Driver("Absent");
        var bus = w.Bus("B01");
        var trip = w.Trip(Wednesday, "Morning", away, bus);
        return (w, trip);
    }

    [Fact]
    public void Tiers_run_free_all_day_then_second_shift_then_seventh_day_then_rule_break()
    {
        var (w, trip) = Setup();
        var spare = w.Bus("B99");

        var ruleBreaker = w.Driver("Dario");
        w.Trip(Wednesday, "Afternoon", ruleBreaker, spare);

        var seventhDay = w.Driver("Carlo");
        for (var d = 10; d <= 15; d++)
            w.Trip(new DateTime(2026, 9, d), "Morning", seventhDay, spare,
                   status: d < 14 ? "Completed" : "Not Yet Started");

        var secondShift = w.Driver("Bea");
        w.Trip(Wednesday, "Evening", secondShift, spare);

        var free = w.Driver("Ana");

        var ranked = SchedulingRules.RankDrivers(trip, w.Snapshot()).Candidates;

        Assert.Equal(new[] { free.UserId, secondShift.UserId, seventhDay.UserId, ruleBreaker.UserId },
                     ranked.Select(c => c.DriverId));
        Assert.Equal(new[] { 1, 2, 3, 4 }, ranked.Select(c => c.Tier));
        Assert.Equal(new[] { 1, 2, 3, 4 }, ranked.Select(c => c.Rank));

        Assert.Null(ranked[0].Warning);
        Assert.Equal("Also on the Evening shift that day", ranked[1].Warning);
        Assert.Equal("Would be 7 days in a row", ranked[2].Warning);
        Assert.Equal("Runs straight into their Afternoon shift", ranked[3].Warning);
    }

    [Fact]
    public void Leaves_out_the_current_driver_closed_accounts_and_anyone_already_on_the_shift()
    {
        var (w, trip) = Setup();
        w.Driver("Closed", status: "Deactivated");
        var booked = w.Driver("Booked");
        w.Trip(Wednesday, "Morning", booked, w.Bus("B02"));
        var free = w.Driver("Free");

        var ranked = SchedulingRules.RankDrivers(trip, w.Snapshot()).Candidates;

        Assert.Equal(new[] { free.UserId }, ranked.Select(c => c.DriverId));
    }

    [Fact]
    public void An_unavailable_flag_rules_a_driver_out_today_only()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var todayTrip = w.Trip(Today, "Afternoon", w.Driver("Absent"), bus);
        var futureTrip = w.Trip(Wednesday, "Afternoon", w.Driver("Absent2"), bus);

        var sick = w.Driver("Sick");
        w.Unavailable.Add(sick.UserId);

        var snapshot = w.Snapshot();

        Assert.DoesNotContain(SchedulingRules.RankDrivers(todayTrip, snapshot).Candidates,
                              c => c.DriverId == sick.UserId);
        Assert.Contains(SchedulingRules.RankDrivers(futureTrip, snapshot).Candidates,
                        c => c.DriverId == sick.UserId);
    }

    [Fact]
    public void A_driver_on_leave_is_listed_apart_and_never_ranked()
    {
        var (w, trip) = Setup();
        var onLeave = w.Driver("Leave");
        w.OnLeave(onLeave, Wednesday.AddDays(-1), Wednesday.AddDays(1));

        var ranking = SchedulingRules.RankDrivers(trip, w.Snapshot());

        Assert.DoesNotContain(ranking.Candidates, c => c.DriverId == onLeave.UserId);
        var listed = Assert.Single(ranking.OnLeave);
        Assert.Equal(onLeave.UserId, listed.DriverId);
        Assert.Equal("On approved vacation leave", listed.Reason);
    }

    [Fact]
    public void A_day_handed_back_from_leave_is_a_working_day_again()
    {
        var (w, trip) = Setup();
        var back = w.Driver("Back");
        w.OnLeave(back, Wednesday.AddDays(-1), Wednesday.AddDays(1), "2026-09-16");

        var ranking = SchedulingRules.RankDrivers(trip, w.Snapshot());

        Assert.Contains(ranking.Candidates, c => c.DriverId == back.UserId);
        Assert.Empty(ranking.OnLeave);
    }

    [Fact]
    public void Inside_a_tier_route_knowledge_comes_first_then_fewest_shifts_then_name()
    {
        var (w, trip) = Setup();
        var spare = w.Bus("B99");

        var busy = w.Driver("Aaron");
        w.Trip(new DateTime(2026, 9, 18), "Morning", busy, spare);
        w.Trip(new DateTime(2026, 9, 19), "Morning", busy, spare);

        var ben = w.Driver("Ben");
        var abby = w.Driver("Abby");

        var knowsRoute = w.Driver("Zed");
        for (var d = 1; d <= 3; d++)
            w.Trip(new DateTime(2026, 9, d), "Morning", knowsRoute, spare, status: "Completed");

        var otherRoute = w.Driver("Yul");
        for (var d = 1; d <= 5; d++)
            w.Trip(new DateTime(2026, 9, d), "Morning", otherRoute, spare, route: SecondRoute, status: "Completed");

        var ranked = SchedulingRules.RankDrivers(trip, w.Snapshot()).Candidates;

        Assert.All(ranked, c => Assert.Equal(1, c.Tier));
        Assert.Equal(new[] { knowsRoute.UserId, abby.UserId, ben.UserId, otherRoute.UserId, busy.UserId },
                     ranked.Select(c => c.DriverId));
        Assert.Equal("Free all day · 3 trips on North Loop in 30 days · 0 shifts this week", ranked[0].Reason);
        Assert.Equal("Free all day · No trips on North Loop in 30 days · 2 shifts this week", ranked[^1].Reason);
    }

    [Fact]
    public void A_missed_trip_is_not_a_day_worked()
    {
        var (w, trip) = Setup();
        var spare = w.Bus("B99");
        var driver = w.Driver("Gap");

        for (var d = 10; d <= 15; d++)
            w.Trip(new DateTime(2026, 9, d), "Morning", driver, spare,
                   status: d == 12 ? "Not Yet Started" : d < 14 ? "Completed" : "Not Yet Started");

        var c = Assert.Single(SchedulingRules.RankDrivers(trip, w.Snapshot()).Candidates);

        Assert.Equal(1, c.Tier);
    }

    [Fact]
    public void Evening_into_the_next_Morning_breaks_the_rule_in_both_directions()
    {
        var w = new World();
        var spare = w.Bus("B99");
        var bus = w.Bus("B01");

        var morningTrip = w.Trip(Wednesday, "Morning", w.Driver("Absent"), bus);
        var eveningBefore = w.Driver("Night");
        w.Trip(Wednesday.AddDays(-1), "Evening", eveningBefore, spare);

        var eveningTrip = w.Trip(Wednesday, "Evening", w.Driver("Absent2"), bus);
        var morningAfter = w.Driver("Early");
        w.Trip(Wednesday.AddDays(1), "Morning", morningAfter, spare);

        var snapshot = w.Snapshot();

        var a = SchedulingRules.RankDrivers(morningTrip, snapshot).Candidates.Single(c => c.DriverId == eveningBefore.UserId);
        Assert.Equal(4, a.Tier);
        Assert.Equal("Comes straight off an Evening shift the night before", a.Warning);

        var b = SchedulingRules.RankDrivers(eveningTrip, snapshot).Candidates.Single(c => c.DriverId == morningAfter.UserId);
        Assert.Equal(4, b.Tier);
        Assert.Equal("Starts a Morning shift the moment this one ends", b.Warning);
    }

    [Fact]
    public void Morning_and_Evening_on_one_day_is_a_second_shift_not_a_rule_break()
    {
        var (w, trip) = Setup();
        var driver = w.Driver("Double");
        w.Trip(Wednesday, "Evening", driver, w.Bus("B99"));

        var c = Assert.Single(SchedulingRules.RankDrivers(trip, w.Snapshot()).Candidates);

        Assert.Equal(2, c.Tier);
    }

    [Fact]
    public void A_name_another_account_shares_carries_the_driver_ID()
    {
        var w = new World();
        var away = w.Driver("Chester", "Alcanzarin");
        var trip = w.Trip(Wednesday, "Morning", away, w.Bus("B01"));
        var namesake = w.Driver("Chester", "Alcanzarin");
        var twinA = w.Driver("Aleah", "Quintos");
        var twinB = w.Driver("aleah", "quintos");
        var ana = w.Driver("Ana");

        var names = SchedulingRules.RankDrivers(trip, w.Snapshot()).Candidates
            .ToDictionary(c => c.DriverId, c => c.Name);

        Assert.Equal($"Chester Alcanzarin ({namesake.UserId})", names[namesake.UserId]);
        Assert.Equal($"Aleah Quintos ({twinA.UserId})", names[twinA.UserId]);
        Assert.Equal($"aleah quintos ({twinB.UserId})", names[twinB.UserId]);
        Assert.Equal("Ana Cruz", names[ana.UserId]);
    }
}
