using FleetWise.Models;
using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class ReadBundleTests
{
    // Shaped as nav_badge_inputs answers, with a roster section and a section that could
    // not be read.
    private const string Answer = """
        {"trips" : [{"trip_id":"T1","date":"2026-09-29","vehicle_id":"V001","driver_id":10,"trip_status":"Active","shift_start_time":"06:00:00","shift_end_time":"14:00:00"}],
         "vehicles" : [{"vehicle_id":"V003","out_of_service":true,"retired_at":"2026-09-01T00:00:00+00:00"}],
         "leave_today" : [{"request_id":2,"user_id":11,"status":"Approved","start_date":"2026-09-28","end_date":"2026-09-30","revoked_dates":["2026-09-30"]}],
         "roster" : {"months" : [{"month":"2026-10-01","status":"Draft"}], "gaps" : []},
         "incidents" : null}
        """;

    [Fact]
    public void Each_list_is_read_into_its_model()
    {
        var read = ReadBundle.Parse(Answer);

        var trip = Assert.Single(read.Rows<Trip>("trips"));
        Assert.Equal("T1", trip.TripId);
        Assert.Equal(new DateTime(2026, 9, 29), trip.Date.Date);
        Assert.Equal(TimeSpan.FromHours(6), trip.ShiftStartTime);
        Assert.Equal(10, trip.DriverId);

        var bus = Assert.Single(read.Rows<Vehicle>("vehicles"));
        Assert.True(bus.OutOfService);
        Assert.NotNull(bus.RetiredAt);

        var leave = Assert.Single(read.Rows<LeaveRequest>("leave_today"));
        Assert.Equal(new DateTime(2026, 9, 30), leave.EndDate.Date);
        Assert.False(LeaveEntitlement.CoversDay(leave, new DateTime(2026, 9, 30)));
        Assert.True(LeaveEntitlement.CoversDay(leave, new DateTime(2026, 9, 29)));
    }

    [Fact]
    public void A_section_is_read_as_a_bundle_of_its_own()
    {
        var roster = ReadBundle.Parse(Answer).Section("roster");

        Assert.NotNull(roster);
        var month = Assert.Single(roster!.Rows<RosterMonth>("months"));
        Assert.Equal("Draft", month.Status);
        Assert.Empty(roster.Rows<RosterGap>("gaps"));
    }

    [Fact]
    public void A_section_the_database_could_not_read_is_missing_rather_than_empty()
    {
        var read = ReadBundle.Parse(Answer);

        Assert.False(read.Has("incidents"));
        Assert.Null(read.Section("incidents"));
        Assert.True(read.Has("trips"));
    }

    [Fact]
    public void A_name_not_sent_reads_as_no_rows()
    {
        Assert.Empty(ReadBundle.Parse(Answer).Rows<Trip>("nothing_here"));
        Assert.Empty(ReadBundle.Parse(null).Rows<Trip>("trips"));
        Assert.Empty(ReadBundle.Parse("  ").Rows<Trip>("trips"));
    }
}
