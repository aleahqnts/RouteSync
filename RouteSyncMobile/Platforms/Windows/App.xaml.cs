using Microsoft.UI.Xaml;

namespace FleetWiseMobile.WinUI;

// Windows entry point, used by the desktop development build.
public partial class App : MauiWinUIApplication
{
	public App()
	{
		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

