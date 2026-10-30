namespace FleetWiseMobile.Services;

// Whether this phone can currently report where the bus is.
public enum LocationState
{
    // Location services are on and the app may use them.
    Ready,

    // The phone's location switch is off.
    ServicesOff,

    // The switch may be on, but the app has not been allowed to use it.
    PermissionDenied
}

// Checks that the phone is able to report its position, and opens the setting that
// fixes it when it cannot.
//
// Granting the app location permission is not the same as location being on. A driver
// can grant the permission and then turn location off from quick settings, and the
// tracking service then gets no fix at all. It writes no telemetry, so the fleet map
// shows the bus as having no active trip, and nothing on the phone said anything was
// wrong. This is what makes that visible.
public interface ILocationGuard
{
    // Permission and the location switch together.
    Task<LocationState> CheckAsync();

    // The location switch alone. Cheap enough to call on every tick.
    bool ServicesEnabled();

    // Opens the system screen with the location switch on it.
    void OpenLocationSettings();

    // Opens this app's own settings page, where a denied permission is granted.
    void OpenAppSettings();
}

// Always ready, so the app still builds and runs on Windows.
public class NoopLocationGuard : ILocationGuard
{
    public Task<LocationState> CheckAsync() => Task.FromResult(LocationState.Ready);
    public bool ServicesEnabled() => true;
    public void OpenLocationSettings() { }
    public void OpenAppSettings() { }
}
