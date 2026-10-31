using FleetWise.Models;

namespace RouteSyncWeb.Tests;

// What the Requests page works out: urgency, the week board and the month grouping.
public class LeaveBoardTests
{
    private static readonly DateTime Today = new(2026, 10, 14);   // a Wednesday

    private static LeaveRequest Leave(int user, string status, int startDay, int endDay,
        string type = "Vacation", params string[] revoked) => new()
    {
        UserId = user,
        LeaveType = type,
        Status = status,
        StartDate = new DateTime(2026, 10, startDay),
        EndDate = new DateTime(2026, 10, endDay),
        RevokedDates = revoked.ToList(),
    };

    [Theory]
    [InlineData(13, "Retro", true)]
    [InlineData(14, "Today", true)]
    [InlineData(15, "Tomorrow", true)]
    [InlineData(16, "In 2 days", true)]
    [InlineData(17, "In 3 days", false)]
    public void Urgency_reads_how_soon_the_leave_starts(int startDay, string label, bool urgent)
    {
        Assert.Equal((label, urgent), LeaveBoard.Urgency(new DateTime(2026, 10, startDay), Today));
    }

    [Fact]
    public void The_week_lists_only_drivers_off_and_draws_approved_over_pending()
    {
        var monday = LeaveBoard.MondayOf(Today);
        Assert.Equal(new DateTime(2026, 10, 12), monday);

        var rows = LeaveBoard.Week(new[]
        {
            Leave(1, "Pending", 15, 16, "Sick"),
            Leave(1, "Approved", 16, 18),
            Leave(2, "Approved", 12, 14, revoked: "2026-10-13"),
            Leave(3, "Rejected", 12, 18),
            Leave(4, "Cancelled", 12, 18),
            Leave(5, "Revoked", 12, 18),
            Leave(6, "AwaitingChange", 20, 22),   // the next week
        }, monday, id => $"Driver {id}");

        Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.DriverId));

        var two = rows[0].Days;
        Assert.True(two[0]!.Approved);
        Assert.Null(two[1]);                       // handed back
        Assert.NotNull(two[2]);

        var one = rows[1].Days;
        Assert.False(one[3]!.Approved);            // Thursday, still waiting
        Assert.Equal("Sick", one[3]!.Type);
        Assert.True(one[4]!.Approved);             // Friday, granted wins
        Assert.Equal("Vacation", one[4]!.Type);
        Assert.NotNull(one[6]);
        Assert.Null(one[0]);

        Assert.Equal(new[] { 1, 0, 1, 1, 1, 1, 1 }, LeaveBoard.OutPerDay(rows));
    }

    [Fact]
    public void History_groups_by_the_month_the_leave_starts_newest_first()
    {
        var items = new[]
        {
            (Id: 1, Start: new DateTime(2026, 9, 30)),
            (Id: 2, Start: new DateTime(2026, 10, 2)),
            (Id: 3, Start: new DateTime(2026, 9, 1)),
            (Id: 4, Start: new DateTime(2026, 11, 5)),
        };

        var months = LeaveBoard.ByMonth(items, x => x.Start);

        Assert.Equal(new[] { new DateTime(2026, 11, 1), new DateTime(2026, 10, 1), new DateTime(2026, 9, 1) },
            months.Select(m => m.Key));
        Assert.Equal(new[] { 1, 3 }, months[2].Select(x => x.Id));
    }

    [Fact]
    public void Only_shifts_inside_the_leave_and_not_handed_back_block_it()
    {
        var leave = Leave(7, "Pending", 20, 22, revoked: "2026-10-21");
        var trips = new[]
        {
            new Trip { TripId = "A", DriverId = 7, Date = new DateTime(2026, 10, 20), TripStatus = "Scheduled" },
            new Trip { TripId = "B", DriverId = 7, Date = new DateTime(2026, 10, 21), TripStatus = "Scheduled" },
            new Trip { TripId = "C", DriverId = 7, Date = new DateTime(2026, 10, 23), TripStatus = "Scheduled" },
            new Trip { TripId = "D", DriverId = 8, Date = new DateTime(2026, 10, 20), TripStatus = "Scheduled" },
        };

        var blocking = LeaveBoard.Blocking(leave, trips, new DateTime(2026, 10, 14, 9, 0, 0));

        Assert.Equal(new[] { "A" }, blocking.Select(t => t.TripId));
    }
}
