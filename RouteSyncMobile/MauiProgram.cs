using Microsoft.Extensions.Logging;

namespace FleetWiseMobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		// A single shared Supabase client, matching the dashboard's setup.
		var supabase = new Supabase.Client(SupabaseConfig.Url, SupabaseConfig.Key);
		supabase.InitializeAsync().Wait();

		// Every read and write carries the driver JWT once sign-in has issued one, with
		// SupabaseConfig.Bearer falling back to the publishable key before that. The
		// closure is evaluated per request, so signing in or out changes the credentials
		// for every query without rebuilding the client.
		if (supabase.Postgrest is Postgrest.Client pg)
			pg.GetHeaders = () => new Dictionary<string, string>
			{
				["apikey"] = SupabaseConfig.Key,
				["Authorization"] = $"Bearer {SupabaseConfig.Bearer}",
			};

		builder.Services.AddSingleton(supabase);

		// App services
		builder.Services.AddSingleton<Services.AuthApi>();
		builder.Services.AddSingleton<Services.SessionService>();
		builder.Services.AddSingleton<Services.AuthService>();
		builder.Services.AddSingleton<Services.DriverDataService>();

		// GPS telemetry: the on-device buffer and the background tracker.
		builder.Services.AddSingleton<Services.TelemetryQueue>();
#if ANDROID
		builder.Services.AddSingleton<Services.ITripTracker, Platforms.Android.AndroidTripTracker>();
		builder.Services.AddSingleton<Services.ILocalNotifier, Platforms.Android.AndroidLocalNotifier>();
		builder.Services.AddSingleton<Services.ILocationGuard, Platforms.Android.AndroidLocationGuard>();
#else
		builder.Services.AddSingleton<Services.ITripTracker, Services.NoopTripTracker>();
		builder.Services.AddSingleton<Services.ILocalNotifier, Services.NoopLocalNotifier>();
		builder.Services.AddSingleton<Services.ILocationGuard, Services.NoopLocationGuard>();
#endif

		// Message poller, which drives the badge, the popup and the system notification.
		builder.Services.AddSingleton<Services.MessageWatch>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
