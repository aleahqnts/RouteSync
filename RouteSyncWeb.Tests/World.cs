using FleetWise.Models;
using FleetWise.Services;

namespace RouteSyncWeb.Tests;

/// <summary>
/// A small fleet built by hand, for asking the scheduling rules questions about.
/// </summary>
/// <remarks>
/// The clock is fixed at 07:00 on Monday 14 September 2026, so the operational day is
/// the 14th, the week runs from the 14th to the 20th, and anything dated before the 14th
/// has already closed.
/// </remarks>
internal sealed class World
{
    public static readonly DateTime Now = new(2026, 9, 14, 7, 0, 0);

    public static readonly DateTime Today = new(2026, 9, 14);

    /// <summary>A Wednesday two days out, where most questions are asked.</summary>
    public static readonly DateTime Wednesday = new(2026, 9, 16);

    public const int NorthLoop = 1;
    public const int SecondRoute = 2;

    public List<Trip> Trips { get; } = new();
    public List<UserModel> Drivers { get; } = new();
    public List<Vehicle> Vehicles { get; } = new();
    public List<LeaveRequest> Leave { get; } = new();
    public HashSet<int> Unavailable { get; } = new();
    public Dictionary<string, MaintenanceLog> Incidents { get; } = new();

    private int _nextTrip = 1;
    private int _nextDriver = 100;

    public UserModel Driver(string first, string last = "Cruz", string status = "Activated")
    {
        var d = new UserModel
        {
            UserId = _nextDriver++,
            FirstName = first,
            LastName = last,
            RoleId = 2,
            AccountStatus = status,
        };
        Drivers.Add(d);
        return d;
    }

    public Vehicle Bus(string id, int? route = NorthLoop, int capacity = 40,
                       bool outOfService = false, bool retired = false)
    {
        var v = new Vehicle
        {
            VehicleId = id,
            PlateNumber = "PLT-" + id,
            RouteId = route,
            Capacity = capacity,
            OutOfService = outOfService,
            RetiredAt = retired ? Now.AddDays(-30) : null,
        };
        Vehicles.Add(v);
        return v;
    }

    public Trip Trip(DateTime date, string shift, UserModel driver, Vehicle bus,
                     int route = NorthLoop, string status = "Not Yet Started")
    {
        var window = TripStatus.Windows[shift];
        var t = new Trip
        {
            TripId = $"TRIP{_nextTrip++:000000}",
            Date = date.Date,
            ShiftType = shift,
            ShiftStartTime = window.Start,
            ShiftEndTime = window.End,
            RouteId = route,
            VehicleId = bus.VehicleId,
            DriverId = driver.UserId,
            TripStatus = status,
        };
        Trips.Add(t);
        return t;
    }

    public LeaveRequest OnLeave(UserModel driver, DateTime from, DateTime to, params string[] revoked)
    {
        var l = new LeaveRequest
        {
            RequestId = Leave.Count + 1,
            UserId = driver.UserId,
            LeaveType = "Vacation",
            StartDate = from.Date,
            EndDate = to.Date,
            Status = "Approved",
            RevokedDates = revoked.ToList(),
        };
        Leave.Add(l);
        return l;
    }

    public void Incident(Vehicle bus, params string[] issues) =>
        Incidents[bus.VehicleId] = new MaintenanceLog
        {
            VehicleId = bus.VehicleId,
            CreatedAt = Now.AddDays(-1),
            IssueDetails = new MaintenanceIssueDetails { Issues = issues.ToList(), Severity = "Minor" },
        };

    public SchedulingSnapshot Snapshot() => new()
    {
        Trips = Trips,
        Drivers = Drivers,
        Vehicles = Vehicles,
        Leave = Leave,
        ReportedUnavailable = Unavailable,
        OpenIncidents = Incidents,
        RouteNames = new Dictionary<int, string> { [NorthLoop] = "North Loop", [SecondRoute] = "Second Route" },
        Now = Now,
    };
}
