namespace FleetWiseMobile.Services;

// Raises an operating system notification while the app is running, in the foreground or
// the background. Android shows it in the system tray; other targets use a no-op
// implementation.
public interface ILocalNotifier
{
    void Show(int id, string title, string body);
}

public class NoopLocalNotifier : ILocalNotifier
{
    public void Show(int id, string title, string body) { }
}
