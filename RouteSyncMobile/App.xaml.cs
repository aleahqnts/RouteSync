namespace FleetWiseMobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	// Whether the app is on screen, for polling that only serves someone looking at it.
	public static bool InForeground { get; private set; } = true;

	// Raised when the app comes back on screen from the background.
	public static event Action? Returned;

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "FleetWiseMobile" };
		window.Stopped += (_, _) => InForeground = false;
		window.Resumed += (_, _) =>
		{
			InForeground = true;
			Returned?.Invoke();
		};
		return window;
	}
}
