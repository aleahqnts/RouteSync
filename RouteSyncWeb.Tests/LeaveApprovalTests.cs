using FleetWise.Models;

namespace RouteSyncWeb.Tests;

// What stops leave from being granted: the review date, and days the driver drove.
public class LeaveApprovalTests
{
    private const int Driver = 1038;

    private static LeaveRequest Sick(params string[] revoked) => new()
    {
        UserId = Driver,
        LeaveType = "Sick",
        StartDate = new DateTime(2026, 10, 2),
        EndDate = new DateTime(2026, 10, 4),
        Status = "Pending",
        RevokedDates = revoked.ToList(),
    };

    private static Trip Trip(int day, string status, int driver = Driver) => new()
    {
        DriverId = driver,
        Date = new DateTime(2026, 10, day),
        TripStatus = status,
    };

    [Fact]
    public void A_completed_trip_inside_the_leave_refuses_it()
    {
        var problem = LeaveEntitlement.DrivenProblem(Sick(), new[] { Trip(3, "Completed") });
        Assert.Equal("Leave cannot be approved for Oct 3 because the driver completed a trip "
            + "on that day.", problem);
    }

    [Fact]
    public void A_shift_that_ended_undriven_is_an_absence_and_stays_grantable()
    {
        Assert.Null(LeaveEntitlement.DrivenProblem(Sick(), new[] { Trip(3, "Scheduled") }));
    }

    [Fact]
    public void Driven_days_outside_the_leave_handed_back_or_by_someone_else_do_not_count()
    {
        var trips = new[]
        {
            Trip(1, "Completed"),
            Trip(3, "Completed"),
            Trip(4, "Completed", driver: 2000),
        };
        Assert.Null(LeaveEntitlement.DrivenProblem(Sick("2026-10-03"), trips));
    }

    [Fact]
    public void Several_driven_days_are_listed_once_each_in_order()
    {
        var trips = new[] { Trip(4, "Active"), Trip(2, "Completed"), Trip(2, "Completed") };
        Assert.StartsWith("Leave cannot be approved for Oct 2, Oct 4 because",
            LeaveEntitlement.DrivenProblem(Sick(), trips));
    }

    [Fact]
    public void Vacation_is_grantable_until_its_first_service_day_ends()
    {
        var start = new DateTime(2026, 10, 30);
        Assert.Null(LeaveEntitlement.ApprovalProblem("Vacation", start, start));
        Assert.Equal("Vacation leave can no longer be approved because its first day has passed.",
            LeaveEntitlement.ApprovalProblem("Vacation", start, start.AddDays(1)));
    }

    [Fact]
    public void Sick_leave_past_the_window_names_the_review_not_the_filing()
    {
        var start = new DateTime(2026, 10, 1);
        Assert.Null(LeaveEntitlement.ApprovalProblem("Sick", start, start.AddDays(14)));
        Assert.Equal("This request is past the 14-day approval window for sick leave.",
            LeaveEntitlement.ApprovalProblem("Sick", start, start.AddDays(15)));
    }
}
