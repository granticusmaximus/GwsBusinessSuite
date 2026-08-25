using CommunityToolkit.Maui;
using GwsBusinessSuite.OllamaKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// SentinelGPT (local): inference always stays on this Mac's own Ollama over loopback.
		// ConversationSessionStore/ApprovedMemoryStore write inside the sandboxed app container
		// (FileSystem.Current.AppDataDirectory), not an arbitrary path the way the unsandboxed
		// SentinelCLI console tool can.
		builder.Services.AddSingleton(_ => new OllamaClient(new Uri("http://127.0.0.1:11434/")));
		builder.Services.AddSingleton(_ =>
			new ConversationSessionStore(Path.Combine(FileSystem.Current.AppDataDirectory, "sentinelgpt-sessions")));
		builder.Services.AddSingleton(_ =>
			new ApprovedMemoryStore(Path.Combine(FileSystem.Current.AppDataDirectory, "sentinelgpt-approved-memory.json")));
		builder.Services.AddSingleton<SecureApiKeyStore>();
		builder.Services.AddSingleton<SentinelGroundingClient>();
		builder.Services.AddSingleton<NativeToolExecutor>();
		builder.Services.AddSingleton<SentinelVoiceService>();
		builder.Services.AddSingleton<DeepAnalysisAdvisor>();
		builder.Services.AddTransient<SentinelGptPage>();

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
