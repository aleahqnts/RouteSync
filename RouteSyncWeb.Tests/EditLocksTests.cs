using FleetWise.Services;

namespace RouteSyncWeb.Tests;

// Edit holds: one person at a time, and no hold outlives the person using it.
public class EditLocksTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 14, 0, 0);

    [Fact]
    public void A_second_person_is_turned_away_and_told_who_has_it()
    {
        var locks = new EditLocks();
        Assert.Null(locks.Take("trip:1", 1, "Ana", true, T0));

        var other = locks.Take("trip:1", 2, "Ben", true, T0.AddSeconds(5));
        Assert.Equal("Ana", other?.Name);
        Assert.Equal("Ana", locks.HeldByOther("trip:1", 2, T0.AddSeconds(5))?.Name);
        Assert.Null(locks.HeldByOther("trip:1", 1, T0.AddSeconds(5)));
        Assert.Null(locks.Take("trip:2", 2, "Ben", true, T0.AddSeconds(5)));
    }

    [Fact]
    public void A_hold_that_stops_renewing_frees_after_the_lease()
    {
        var locks = new EditLocks();
        locks.Take("trip:1", 1, "Ana", true, T0);

        Assert.NotNull(locks.HeldByOther("trip:1", 2, T0.AddSeconds(29)));
        Assert.Null(locks.Take("trip:1", 2, "Ben", true, T0.AddSeconds(31)));
    }

    [Fact]
    public void Leaving_frees_it_at_once()
    {
        var locks = new EditLocks();
        locks.Take("user:7", 1, "Ana", true, T0);
        locks.Release("user:7", 2);
        Assert.NotNull(locks.HeldByOther("user:7", 2, T0));
        locks.Release("user:7", 1);
        Assert.Null(locks.HeldByOther("user:7", 2, T0));
    }

    [Fact]
    public void An_open_tab_with_no_clicks_for_30_minutes_loses_the_hold()
    {
        var locks = new EditLocks();
        locks.Take("trip:1", 1, "Ana", true, T0);

        var t = T0;
        for (var i = 0; i < 179; i++)
        {
            t = t.AddSeconds(10);
            Assert.Null(locks.Take("trip:1", 1, "Ana", false, t));
        }

        // Thirty minutes after the last click the renewal is refused with her own hold.
        var lost = locks.Take("trip:1", 1, "Ana", false, T0.AddMinutes(30));
        Assert.Equal(1, lost?.UserId);
        Assert.Null(locks.Take("trip:1", 2, "Ben", true, T0.AddMinutes(30)));
    }

    [Fact]
    public void Clicking_keeps_a_long_edit_held()
    {
        var locks = new EditLocks();
        locks.Take("trip:1", 1, "Ana", true, T0);
        for (var m = 1; m <= 120; m++)
            Assert.Null(locks.Take("trip:1", 1, "Ana", true, T0.AddMinutes(m).AddSeconds(-50)));
    }
}
