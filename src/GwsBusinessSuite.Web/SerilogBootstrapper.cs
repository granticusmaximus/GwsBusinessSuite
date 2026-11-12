using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace GwsBusinessSuite.Web;

public static class SerilogBootstrapper
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var logFilePath = LogFilePath(builder.Configuration);
        var release = GwsBusinessSuite.Web.Services.BuildStamp.For(typeof(SerilogBootstrapper).Assembly).Commit ?? "unknown";
        builder.Services.AddSingleton<Diagnostics.LogIssueSink>();
        builder.Host.UseSerilog((_, services, loggerConfiguration) => loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            // IHttpClientFactory logs two Information lines per outbound request (start, response
            // headers), which made up most of production's ~200 MB a day. The file sink keeps 14
            // files, not 14 days, so that noise cut the history to about 3.5 days.
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            // Context first: a property pushed for one event (Origin=browser on a client report)
            // wins over the global default below, since each enricher only adds what is absent.
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Origin", "server")
            .Enrich.WithProperty("Release", release)
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 50_000_000,
                rollOnFileSizeLimit: true,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            // The same events as compact JSON (logging plan, Phase 1, 12-11-26), with every
            // property: TraceId, RequestId, UserName, CircuitId, Origin, Release. The text file
            // is for reading; this one is for searching and for the issues store.
            .WriteTo.File(
                new RenderedCompactJsonFormatter(),
                JsonLogPath(logFilePath),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 50_000_000,
                rollOnFileSizeLimit: true)
            // Warnings and errors, grouped into issues for /admin/logs.
            .WriteTo.Sink(services.GetRequiredService<Diagnostics.LogIssueSink>(), LogEventLevel.Warning));
    }

    public static string LogFilePath(IConfiguration configuration) =>
        configuration["Diagnostics:LogFilePath"] ?? "/app/data/logs/gwssuite-.log";

    // gwssuite-.log -> gwssuite-.clef, beside it.
    public static string JsonLogPath(string logFilePath) => Path.ChangeExtension(logFilePath, ".clef");
}
