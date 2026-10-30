using FleetWiseMobile.Models;

namespace FleetWiseMobile.Services;

// The one hour break dispatch schedules inside a shift.
//
// Only read here. The dashboard chooses the slot, three, four or five hours after the
// shift starts, and nothing in this app starts or ends a break: the screen says when it
// is, and tracking and counting carry on through it.
public static class TripBreak
{
    public static readonly TimeSpan Length = TimeSpan.FromHours(1);

    // When the break begins, or null for a trip with none.
    //
    // A slot earlier in the day than the shift's start falls after midnight, on the day
    // after the trip's date. That is every Evening slot.
    public static DateTime? StartAt(Trip trip) =>
        trip.BreakStart is TimeSpan b
            ? trip.Date.Date + b + (b < trip.ShiftStartTime ? TimeSpan.FromDays(1) : TimeSpan.Zero)
            : null;

    // Whether the clock is inside the break.
    public static bool IsOn(Trip trip, DateTime now) =>
        StartAt(trip) is DateTime s && now >= s && now < s + Length;

    // The break as "1:00 AM to 2:00 AM".
    public static string Range(TimeSpan breakStart) =>
        $"{PhTime.Now.Date.Add(breakStart):h:mm tt} to {PhTime.Now.Date.Add(breakStart + Length):h:mm tt}";

    // When the break ends, as "2:00 AM".
    public static string EndLabel(TimeSpan breakStart) =>
        PhTime.Now.Date.Add(breakStart + Length).ToString("h:mm tt");
}
