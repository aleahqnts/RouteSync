using FleetWise.Models;
using FleetWise.Services;

namespace RouteSyncWeb.Tests;

public class StoredTimesTests
{
    // 13:58 as stored. Postgrest hands it back either as UTC or moved to the machine's own
    // zone, depending on where the app runs, and both must read the same.
    private static readonly DateTime StoredUtc = new(2026, 9, 25, 13, 58, 0, DateTimeKind.Utc);
    private static readonly DateTime StoredLocal = StoredUtc.ToLocalTime();

    // The same value as it also arrives: moved into the machine's zone but left unmarked.
    private static readonly DateTime StoredUnmarked = DateTime.SpecifyKind(StoredLocal, DateTimeKind.Unspecified);

    [Fact]
    public void A_true_UTC_column_reads_eight_hours_later_wherever_the_app_runs()
    {
        var expected = new DateTime(2026, 9, 25, 21, 58, 0);

        Assert.Equal(expected, StoredTimes.FromUtc(StoredUtc));
        Assert.Equal(expected, StoredTimes.FromUtc(StoredLocal));
        Assert.Equal(expected, StoredTimes.FromUtc(StoredUnmarked));
    }

    [Fact]
    public void A_Philippine_clock_column_reads_as_stored_wherever_the_app_runs()
    {
        var expected = new DateTime(2026, 9, 25, 13, 58, 0);

        Assert.Equal(expected, StoredTimes.FromWall(StoredUtc));
        Assert.Equal(expected, StoredTimes.FromWall(StoredLocal));
        Assert.Equal(expected, StoredTimes.FromWall(StoredUnmarked));
    }

    [Fact]
    public void A_Philippine_clock_column_names_the_instant_eight_hours_earlier_wherever_the_app_runs()
    {
        // A GPS reading taken at 13:58 on the phone's Philippine clock was taken at 05:58 UTC.
        var expected = new DateTime(2026, 9, 25, 5, 58, 0, DateTimeKind.Utc);

        Assert.Equal(expected, StoredTimes.WallAsUtc(StoredUtc));
        Assert.Equal(expected, StoredTimes.WallAsUtc(StoredLocal));
        Assert.Equal(expected, StoredTimes.WallAsUtc(StoredUnmarked));
        Assert.Equal(DateTimeKind.Utc, StoredTimes.WallAsUtc(StoredUnmarked).Kind);
    }

    [Fact]
    public void A_submitted_inspection_is_UTC_and_a_skipped_one_is_Philippine_time()
    {
        var submitted = new BusChecklist { ChecklistStatus = "Passed with Defects", SubmittedAt = StoredUtc };
        var skipped = new BusChecklist { ChecklistStatus = "Skipped", SubmittedAt = StoredUtc };

        Assert.Equal(21, StoredTimes.Inspected(submitted).Hour);
        Assert.Equal(13, StoredTimes.Inspected(skipped).Hour);
    }

    [Fact]
    public void An_order_opened_by_an_inspection_is_UTC_and_one_opened_on_the_dashboard_is_not()
    {
        var fromInspection = new MaintenanceLog { ChecklistId = 7, CreatedAt = StoredUtc };
        var fromDashboard = new MaintenanceLog { ChecklistId = null, CreatedAt = StoredUtc };

        Assert.Equal(21, StoredTimes.Opened(fromInspection).Hour);
        Assert.Equal(13, StoredTimes.Opened(fromDashboard).Hour);
    }
}
