using FleetWiseMobile.Models;

namespace FleetWiseMobile.Services;

/// <summary>The one hour break dispatch schedules inside a shift.</summary>
/// <remarks>
/// Only read here. The dashboard chooses the slot, three, four or five hours after the
/// shift starts, and nothing in this app starts or ends a break: the screen says when it
/// is, and tracking and counting carry on through it.
/// </remarks>
public static class TripBreak
{
    public static readonly TimeSpan Length = TimeSpan.FromHours(1);

    /// <summary>When the break begins, or null for a trip with none.</summary>
    /// <remarks>
    /// A slot earlier in the day than the shift's start falls after midnight, on the day
    /// after the trip's date. That is every Evening slot.
    /// </remarks>
    public static DateTime? StartAt(Trip trip) =>
        trip.BreakStart is TimeSpan b
            ? trip.Date.Date + b + (b < trip.ShiftStartTime ? TimeSpan.FromDays(1) : TimeSpan.Zero)
            : null;

    /// <summary>Whether the clock is inside the break.</summary>
    public static bool IsOn(Trip trip, DateTime now) =>
        StartAt(trip) is DateTime s && now >= s && now < s + Length;

    /// <summary>The break as "1:00 AM to 2:00 AM".</summary>
    public static string Range(TimeSpan breakStart) =>
        $"{DateTime.Today.Add(breakStart):h:mm tt} to {DateTime.Today.Add(breakStart + Length):h:mm tt}";

    /// <summary>When the break ends, as "2:00 AM".</summary>
    public static string EndLabel(TimeSpan breakStart) =>
        DateTime.Today.Add(breakStart + Length).ToString("h:mm tt");
}
