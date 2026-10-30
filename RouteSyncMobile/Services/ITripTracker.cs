namespace FleetWiseMobile.Services;

// Starts and stops background GPS tracking for an active trip. Android runs a foreground
// service; other platforms use a no-op implementation.
public interface ITripTracker
{
    void Start(string tripId);
    void Stop();
}

// No-op implementation, so the app still builds and runs on Windows.
public class NoopTripTracker : ITripTracker
{
    public void Start(string tripId) { }
    public void Stop() { }
}
