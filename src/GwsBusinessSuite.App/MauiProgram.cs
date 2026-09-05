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
			// ChatEditor needs its own platform view on UIKit to declare a Shift+Return key
			// command - see ChatEditorHandler. Scoped to this control so every other Editor in
			// the app keeps stock behaviour.
			.ConfigureMauiHandlers(handlers => handlers.AddHandler<ChatEditor, ChatEditorHandler>())
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// SentinelGPT (local): inference always stays on this Mac's own Ollama over loopback.
		// ConversationSessionStore/ApprovedMemoryStore/SecureApiKeyStore all write inside the
		// sandboxed app container (FileSystem.Current.AppDataDirectory), not an arbitrary path
		// the way the unsandboxed SentinelCLI console tool can. SecureApiKeyStore specifically
		// used Keychain-backed SecureStorage until 2026-08-25 - moved off it after confirming the
		// required keychain-access-groups entitlement needs real Apple provisioning this
		// ad-hoc-signed build doesn't have (it broke the app's ability to launch at all).
		builder.Services.AddSingleton(_ => new OllamaClient(new Uri("http://127.0.0.1:11434/")));
		builder.Services.AddSingleton(_ =>
			new ConversationSessionStore(Path.Combine(FileSystem.Current.AppDataDirectory, "sentinelgpt-sessions")));
		builder.Services.AddSingleton(_ =>
			new ApprovedMemoryStore(Path.Combine(FileSystem.Current.AppDataDirectory, "sentinelgpt-approved-memory.json")));
		builder.Services.AddSingleton(_ =>
			new SecureApiKeyStore(Path.Combine(FileSystem.Current.AppDataDirectory, "sentinelgpt-grounding-key.txt")));
		builder.Services.AddSingleton(_ =>
			new WorkspaceBookmarkStore(Path.Combine(FileSystem.Current.AppDataDirectory, "sentinelgpt-workspace-bookmark.dat")));
		builder.Services.AddSingleton<SentinelGroundingClient>();
		// Installs models over Ollama's HTTP API. The sandbox denies process execution, so this
		// is the only route by which the app can install a model itself instead of telling the
		// user to run sentinelcli in a Terminal.
		builder.Services.AddSingleton<GwsBusinessSuite.SentinelAgentKit.OllamaModelInstaller>();
		builder.Services.AddSingleton<NativeToolExecutor>();
		builder.Services.AddSingleton<SentinelVoiceService>();
		builder.Services.AddSingleton<DeepAnalysisAdvisor>();
		builder.Services.AddTransient<SentinelGptPage>();

		// MacCatalyst-only device login (see NativeCookieInjector) - UseCookies: false so the raw
		// Set-Cookie header survives on the response instead of being consumed into an
		// HttpClientHandler-internal CookieContainer this app never reads from.
		builder.Services.AddSingleton(_ =>
			new DeviceSecretStore(Path.Combine(FileSystem.Current.AppDataDirectory, "sentinelgpt-device-secret.txt")));
		builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { UseCookies = false }));
		builder.Services.AddSingleton<NativeAppAuthService>();
		builder.Services.AddSingleton<NativeFallbackChatService>();
		builder.Services.AddTransient<MainPage>();

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

		// Enter sends / Shift+Enter newlines in the SentinelGPT composer. Registered once here
		// because handler mappings are static and global, not per-page.
		SendOnEnterBehavior.Configure();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
