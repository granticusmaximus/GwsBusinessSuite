using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.App;

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
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if ANDROID
		Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping(
			"TrustedAndroidCapabilities",
			(handler, _) =>
			{
				handler.PlatformView.SetWebChromeClient(new TrustedMediaWebChromeClient(handler));
				handler.PlatformView.SetDownloadListener(new TrustedDownloadListener());
			});
#elif IOS || MACCATALYST
		Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping(
			"TrustedAppleDownloads",
			(handler, _) => handler.PlatformView.NavigationDelegate = new TrustedDownloadNavigationDelegate(handler));
#elif WINDOWS
		Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping(
			"TrustedWindowsDownloads",
			(handler, _) => TrustedWindowsDownloads.Configure(handler.PlatformView));
#endif

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
