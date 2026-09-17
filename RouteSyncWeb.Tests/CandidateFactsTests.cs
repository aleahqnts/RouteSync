using System.Text.Json;
using FleetWise.Models;
using FleetWise.Services;
using static RouteSyncWeb.Tests.World;

namespace RouteSyncWeb.Tests;

/// <summary>
/// What a ranking says about each candidate and about the ones it left out, so every screen
/// can show the same figures and say why nobody fits.
/// </summary>
public class CandidateFactsTests
{
    [Fact]
    public void A_driver_carries_the_figures_they_were_ranked_on()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Absent"), w.Bus("B01"));
        var spare = w.Bus("B99");

        var ana = w.Driver("Ana");
        w.Trip(new DateTime(2026, 9, 10), "Morning", ana, spare, status: "Completed");
        w.Trip(new DateTime(2026, 9, 15), "Morning", ana, spare);
        w.Trip(Wednesday, "Evening", ana, spare);

        var bea = w.Driver("Bea");

        var ranked = SchedulingRules.RankDrivers(trip, w.Snapshot()).Candidates;
        var a = ranked.Single(c => c.DriverId == ana.UserId);
        var b = ranked.Single(c => c.DriverId == bea.UserId);

        Assert.Equal(new DriverFacts(2, "North Loop", 2, "Evening", 2), a.Facts);
        Assert.Equal(new DriverFacts(0, "North Loop", 0, null, 1), b.Facts);

        Assert.Equal("Free this shift, 2 North Loop trips in the last 30 days", SchedulingRules.PickSentence(a));
        Assert.Equal("Free all day, no North Loop trips in the last 30 days", SchedulingRules.PickSentence(b));
    }

    [Fact]
    public void With_only_costly_drivers_the_shortfall_counts_each_cost()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Absent"), w.Bus("B01"));
        var spare = w.Bus("B99");

        var carlo = w.Driver("Carlo");
        for (var d = 10; d <= 15; d++)
            w.Trip(new DateTime(2026, 9, d), "Morning", carlo, spare,
                   status: d < 14 ? "Completed" : "Not Yet Started");

        w.Trip(Wednesday, "Afternoon", w.Driver("Dario"), spare);

        var ranking = SchedulingRules.RankDrivers(trip, w.Snapshot());

        Assert.Equal("No driver is free without a cost: 1 would break a rest rule and 1 would work 7 or more days in a row.",
                     SchedulingRules.DriverShortfall(ranking));
    }

    [Fact]
    public void With_nobody_to_rank_the_shortfall_says_why_each_was_left_out()
    {
        var w = new World();
        var trip = w.Trip(Today, "Morning", w.Driver("Absent"), w.Bus("B01"));

        w.Trip(Today, "Morning", w.Driver("Booked"), w.Bus("B02"));
        w.Unavailable.Add(w.Driver("Unwell").UserId);
        w.OnLeave(w.Driver("Away"), Today, Today);

        var ranking = SchedulingRules.RankDrivers(trip, w.Snapshot());

        Assert.Empty(ranking.Candidates);
        Assert.Equal(new DriverExclusions(1, 1), ranking.Excluded);
        Assert.Equal("No driver can take this shift: 1 is already on that shift, 1 reported unable to drive and 1 is on approved leave.",
                     SchedulingRules.DriverShortfall(ranking));
    }

    [Fact]
    public void A_free_driver_means_no_shortfall()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Absent"), w.Bus("B01"));
        w.Driver("Free");

        Assert.Null(SchedulingRules.DriverShortfall(SchedulingRules.RankDrivers(trip, w.Snapshot())));
    }

    [Fact]
    public void A_bus_carries_its_figures_and_a_one_line_summary()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Juan"), w.Bus("B01", capacity: 50));
        w.Bus("B02", route: SecondRoute, capacity: 30);

        var ranking = SchedulingRules.RankVehicles(trip, w.Snapshot());
        var bus = Assert.Single(ranking.Candidates);

        Assert.Equal(new VehicleFacts(30, "B01", "Second Route", false, 0), bus.Facts);
        Assert.Equal("From Second Route, 30 seats, fewer than B01", SchedulingRules.PickSentence(bus));
        Assert.Null(SchedulingRules.VehicleShortfall(ranking));
    }

    [Fact]
    public void Bus_shortfalls_count_attention_or_why_buses_were_left_out()
    {
        var costly = new World();
        var trip = costly.Trip(Wednesday, "Morning", costly.Driver("Juan"), costly.Bus("B01"));
        costly.Incident(costly.Bus("B02"), "Brake noise");

        Assert.Equal("No bus is free without a cost: 1 needs attention.",
                     SchedulingRules.VehicleShortfall(SchedulingRules.RankVehicles(trip, costly.Snapshot())));

        var none = new World();
        var other = none.Trip(Wednesday, "Morning", none.Driver("Juan"), none.Bus("B01"));
        none.Bus("B02", outOfService: true);
        none.Trip(Wednesday, "Morning", none.Driver("Pedro"), none.Bus("B03"));
        none.Bus("B04", retired: true);

        var ranking = SchedulingRules.RankVehicles(other, none.Snapshot());

        Assert.Equal(new VehicleExclusions(1, 1), ranking.Excluded);
        Assert.Equal("No bus can take this trip: 1 is already on that shift and 1 is out of service.",
                     SchedulingRules.VehicleShortfall(ranking));
    }

    [Fact]
    public void A_pinned_cover_is_kept_and_the_shifts_after_it_are_ranked_around_it()
    {
        var w = new World();
        var away = w.Driver("Absent");
        var tuesdayEvening = w.Trip(new DateTime(2026, 9, 15), "Evening", away, w.Bus("B01"));
        var wednesdayMorning = w.Trip(Wednesday, "Morning", away, w.Bus("B02"));

        var spare = w.Bus("B99");
        var ana = w.Driver("Ana");
        for (var d = 1; d <= 3; d++)
            w.Trip(new DateTime(2026, 9, d), "Morning", ana, spare, status: "Completed");
        var bea = w.Driver("Bea");

        var s = w.Snapshot();

        // Unpinned, Ana takes the Tuesday Evening and Bea the Wednesday Morning.
        var plain = SchedulingRules.SuggestCovers(new[] { tuesdayEvening, wednesdayMorning }, s);
        Assert.Equal(ana.UserId, plain[0].Suggested?.DriverId);
        Assert.Equal(bea.UserId, plain[1].Suggested?.DriverId);

        // With Bea chosen for the Tuesday Evening, the Wednesday Morning would break her rest,
        // so Ana is suggested there instead.
        var pinned = SchedulingRules.SuggestCovers(new[] { tuesdayEvening, wednesdayMorning }, s,
            new Dictionary<string, int> { [tuesdayEvening.TripId] = bea.UserId });

        Assert.Equal(bea.UserId, pinned[0].PinnedDriverId);
        Assert.Equal(bea.UserId, pinned[0].Suggested?.DriverId);
        Assert.Null(pinned[0].PinnedProblem);
        Assert.Null(pinned[1].PinnedDriverId);
        Assert.Equal(ana.UserId, pinned[1].Suggested?.DriverId);
    }

    [Fact]
    public void A_shift_left_for_now_is_suggested_nobody_and_frees_its_driver_for_later_shifts()
    {
        var w = new World();
        var away = w.Driver("Absent");
        var tuesdayEvening = w.Trip(new DateTime(2026, 9, 15), "Evening", away, w.Bus("B01"));
        var wednesdayMorning = w.Trip(Wednesday, "Morning", away, w.Bus("B02"));
        var bea = w.Driver("Bea");

        var s = w.Snapshot();

        // Bea alone is free. Suggested for the Tuesday Evening, she would break her rest on
        // the Wednesday Morning, so that shift gets nobody.
        var plain = SchedulingRules.SuggestCovers(new[] { tuesdayEvening, wednesdayMorning }, s);
        Assert.Equal(bea.UserId, plain[0].Suggested?.DriverId);
        Assert.Null(plain[1].Suggested);

        // With the Tuesday Evening left for now, nobody is assumed on it, and Bea is free
        // for the Wednesday Morning.
        var left = SchedulingRules.SuggestCovers(new[] { tuesdayEvening, wednesdayMorning }, s,
            new Dictionary<string, int> { [tuesdayEvening.TripId] = SchedulingRules.LeftForNow });

        Assert.Equal(SchedulingRules.LeftForNow, left[0].PinnedDriverId);
        Assert.Null(left[0].Suggested);
        Assert.Null(left[0].PinnedProblem);
        Assert.Contains(left[0].Candidates, c => c.DriverId == bea.UserId);
        Assert.Equal(bea.UserId, left[1].Suggested?.DriverId);
    }

    [Fact]
    public void A_pinned_cover_that_no_longer_fits_says_why_and_is_not_replaced()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Absent"), w.Bus("B01"));
        var booked = w.Driver("Booked");
        w.Trip(Wednesday, "Morning", booked, w.Bus("B02"));
        w.Driver("Free");

        var cover = Assert.Single(SchedulingRules.SuggestCovers(new[] { trip }, w.Snapshot(),
            new Dictionary<string, int> { [trip.TripId] = booked.UserId }));

        Assert.Equal(booked.UserId, cover.PinnedDriverId);
        Assert.Null(cover.Suggested);
        Assert.Equal("No longer free for this shift.", cover.PinnedProblem);
    }

    [Fact]
    public void A_shift_nobody_can_cover_carries_its_shortfall()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Absent"), w.Bus("B01"));
        w.Trip(Wednesday, "Afternoon", w.Driver("Dario"), w.Bus("B99"));

        var cover = Assert.Single(SchedulingRules.SuggestCovers(new[] { trip }, w.Snapshot()));

        Assert.Equal("No driver is free without a cost: 1 would break a rest rule.", cover.Shortfall);
    }

    [Fact]
    public void Filling_an_open_shift_records_where_the_driver_sat_and_on_which_screen()
    {
        var w = new World();
        w.Bus("B01");
        var spare = w.Bus("B99");
        var ana = w.Driver("Ana");
        w.Trip(new DateTime(2026, 9, 10), "Morning", ana, spare, status: "Completed");
        var bea = w.Driver("Bea");

        var slot = SchedulingRules.OpenSlot(Wednesday, "Morning", NorthLoop, "B01");
        Assert.Equal(TripStatus.Windows["Morning"].Start, slot.ShiftStartTime);
        Assert.Equal(0, slot.DriverId);

        var tag = SchedulingRules.TagFill(slot, bea.UserId, w.Snapshot(), PickScreen.Planner);

        Assert.False(tag.TookTopSuggestion);
        Assert.Equal(2, tag.Driver!.Rank);
        Assert.Null(tag.Vehicle);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(tag.ToAuditChanges()));
        var rec = doc.RootElement.GetProperty("recommendation");
        Assert.Equal("manual", rec.GetProperty("via").GetString());
        Assert.Equal("planner", rec.GetProperty("screen").GetString());
        Assert.Equal(0, rec.GetProperty("issues").GetArrayLength());
    }

    [Fact]
    public void A_driver_placed_on_the_shift_in_an_unsaved_plan_is_left_out_and_counted()
    {
        var w = new World();
        w.Bus("B01");
        var spare = w.Bus("B99");
        var ana = w.Driver("Ana");
        w.Trip(new DateTime(2026, 9, 10), "Morning", ana, spare, status: "Completed");
        var bea = w.Driver("Bea");

        var slot = SchedulingRules.OpenSlot(Wednesday, "Morning", NorthLoop, "B01");
        var planned = SchedulingRules.WithPlaced(w.Snapshot(), Wednesday, "Morning", new[] { ana.UserId, 0 });

        // Ana would top the list; placed elsewhere on the shift, Bea is the best match instead,
        // and a save that fills the gap with Bea records it as the top pick.
        var ranking = SchedulingRules.RankDrivers(slot, planned);
        Assert.Equal(new[] { bea.UserId }, ranking.Candidates.Select(c => c.DriverId));
        Assert.Equal(1, ranking.Candidates[0].Rank);
        Assert.Equal(new DriverExclusions(1, 0), ranking.Excluded);
        Assert.True(SchedulingRules.TagFill(slot, bea.UserId, planned, PickScreen.Planner).TookTopSuggestion);

        // Placed on another shift, nobody is left out.
        var other = SchedulingRules.WithPlaced(w.Snapshot(), Wednesday, "Evening", new[] { ana.UserId });
        Assert.Equal(2, SchedulingRules.RankDrivers(slot, other).Candidates.Count);
    }

    [Fact]
    public void Only_the_known_screens_are_accepted()
    {
        Assert.True(PickScreen.IsKnown("board"));
        Assert.True(PickScreen.IsKnown("planner"));
        Assert.False(PickScreen.IsKnown("Board"));
        Assert.False(PickScreen.IsKnown(null));
        Assert.False(PickScreen.IsKnown("elsewhere"));
    }
}
