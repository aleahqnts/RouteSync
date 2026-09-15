using System.Text.Json;
using FleetWise.Services;
using static RouteSyncWeb.Tests.World;

namespace RouteSyncWeb.Tests;

public class IssueAndTagTests
{
    [Fact]
    public void A_grounded_bus_is_a_vehicle_issue()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Juan"), w.Bus("B01", outOfService: true));

        var issues = SchedulingRules.IssuesOf(trip, w.Snapshot());

        Assert.Equal(new[] { AssignmentIssue.VehicleOutOfService }, issues);
        Assert.True(SchedulingRules.IsVehicleIssue(issues));
        Assert.False(SchedulingRules.IsDriverIssue(issues));
    }

    [Fact]
    public void An_unavailable_flag_is_an_issue_on_todays_trips_and_not_on_later_ones()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var sick = w.Driver("Sick");
        w.Unavailable.Add(sick.UserId);
        var today = w.Trip(Today, "Afternoon", sick, bus);
        var later = w.Trip(Wednesday, "Afternoon", sick, bus);

        var snapshot = w.Snapshot();

        Assert.Equal(new[] { AssignmentIssue.DriverUnavailable }, SchedulingRules.IssuesOf(today, snapshot));
        Assert.Empty(SchedulingRules.IssuesOf(later, snapshot));
    }

    [Fact]
    public void Leave_and_a_closed_account_are_driver_issues()
    {
        var w = new World();
        var bus = w.Bus("B01");
        var onLeave = w.Driver("Leave");
        w.OnLeave(onLeave, Wednesday, Wednesday);
        var closed = w.Driver("Closed", status: "Deactivated");

        var snapshot = w.Snapshot();

        Assert.Equal(new[] { AssignmentIssue.DriverOnLeave },
                     SchedulingRules.IssuesOf(w.Trip(Wednesday, "Morning", onLeave, bus), snapshot));
        Assert.Equal(new[] { AssignmentIssue.DriverUnavailable },
                     SchedulingRules.IssuesOf(w.Trip(Wednesday, "Evening", closed, bus), snapshot));
    }

    [Fact]
    public void No_tag_when_neither_driver_nor_bus_changed()
    {
        var w = new World();
        var driver = w.Driver("Juan");
        var bus = w.Bus("B01");
        var trip = w.Trip(Wednesday, "Morning", driver, bus);

        Assert.Null(SchedulingRules.TagReassignment(trip, driver.UserId, bus.VehicleId, w.Snapshot()));
        Assert.Null(SchedulingRules.TagReassignment(trip, null, null, w.Snapshot()));
    }

    [Fact]
    public void Taking_the_top_driver_is_a_suggestion_and_any_other_is_manual()
    {
        var w = new World();
        var onLeave = w.Driver("Away");
        w.OnLeave(onLeave, Wednesday, Wednesday);
        var trip = w.Trip(Wednesday, "Morning", onLeave, w.Bus("B01"));

        var first = w.Driver("Ana");
        var second = w.Driver("Ben");
        var alsoAway = w.Driver("Cara");
        w.OnLeave(alsoAway, Wednesday, Wednesday);

        var snapshot = w.Snapshot();

        var top = SchedulingRules.TagReassignment(trip, first.UserId, null, snapshot)!;
        Assert.True(top.TookTopSuggestion);
        Assert.Equal(new RankedPick(1, 1, 2), top.Driver);
        Assert.Null(top.Vehicle);
        Assert.Equal(new[] { AssignmentIssue.DriverOnLeave }, top.Issues);

        var other = SchedulingRules.TagReassignment(trip, second.UserId, null, snapshot)!;
        Assert.False(other.TookTopSuggestion);
        Assert.Equal(new RankedPick(2, 1, 2), other.Driver);

        var unlisted = SchedulingRules.TagReassignment(trip, alsoAway.UserId, null, snapshot)!;
        Assert.False(unlisted.TookTopSuggestion);
        Assert.Equal(new RankedPick(null, null, 2), unlisted.Driver);
    }

    [Fact]
    public void Both_sides_must_be_the_top_pick_to_count_as_a_suggestion()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Juan"), w.Bus("B01", outOfService: true));
        var topDriver = w.Driver("Ana");
        w.Driver("Ben");
        w.Bus("B02");
        var secondBus = w.Bus("B03", route: SecondRoute);

        var tag = SchedulingRules.TagReassignment(trip, topDriver.UserId, secondBus.VehicleId, w.Snapshot())!;

        Assert.False(tag.TookTopSuggestion);
        Assert.Equal(1, tag.Driver!.Rank);
        Assert.Equal(2, tag.Vehicle!.Rank);
    }

    [Fact]
    public void The_audit_value_sits_under_its_own_key_so_it_never_reads_as_a_row_edit()
    {
        var w = new World();
        var trip = w.Trip(Wednesday, "Morning", w.Driver("Juan"), w.Bus("B01"));
        var ana = w.Driver("Ana");

        var json = JsonSerializer.Serialize(
            SchedulingRules.TagReassignment(trip, ana.UserId, null, w.Snapshot())!.ToAuditChanges());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("old", out _));
        Assert.False(root.TryGetProperty("new", out _));

        var rec = root.GetProperty("recommendation");
        Assert.Equal("suggestion", rec.GetProperty("via").GetString());
        Assert.Equal(1, rec.GetProperty("driver").GetProperty("rank").GetInt32());
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("vehicle").ValueKind);
    }
}
