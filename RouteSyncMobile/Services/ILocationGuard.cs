namespace FleetWiseMobile.Services;

/// <summary>Whether this phone can currently report where the bus is.</summary>
public enum LocationState
{
    /// <summary>Location services are on and the app may use them.</summary>
    Ready,

    /// <summary>The phone's location switch is off.</summary>
    ServicesOff,

    /// <summary>The switch may be on, but the app has not been allowed to use it.</summary>
    PermissionDenied
}

/// <summary>
/// Checks that the phone is able to report its position, and opens the setting that
/// fixes it when it cannot.
/// </summary>
/// <remarks>
/// Granting the app location permission is not the same as location being on. A driver
/// can grant the permission and then turn location off from quick settings, and the
/// tracking service then gets no fix at all. It writes no telemetry, so the fleet map
/// shows the bus as having no active trip, and nothing on the phone said anything was
/// wrong. This is what makes that visible.
/// </remarks>
public interface ILocationGuard
{
    /// <summary>Permission and the location switch together.</summary>
    Task<LocationState> CheckAsync();

    /// <summary>The location switch alone. Cheap enough to call on every tick.</summary>
    bool ServicesEnabled();

    /// <summary>Opens the system screen with the location switch on it.</summary>
    void OpenLocationSettings();

    /// <summary>Opens this app's own settings page, where a denied permission is granted.</summary>
    void OpenAppSettings();
}

/// <summary>Always ready, so the app still builds and runs on Windows.</summary>
public class NoopLocationGuard : ILocationGuard
{
    public Task<LocationState> CheckAsync() => Task.FromResult(LocationState.Ready);
    public bool ServicesEnabled() => true;
    public void OpenLocationSettings() { }
    public void OpenAppSettings() { }
}
