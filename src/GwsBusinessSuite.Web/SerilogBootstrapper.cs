using Serilog;
using Serilog.Events;

namespace GwsBusinessSuite.Web;

public static class SerilogBootstrapper
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var logFilePath = builder.Configuration["Diagnostics:LogFilePath"] ?? "/app/data/logs/gwssuite-.log";
        builder.Host.UseSerilog((_, loggerConfiguration) => loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            // IHttpClientFactory logs two Information lines per outbound request (start, response
            // headers), which made up most of production's ~200 MB a day. The file sink keeps 14
            // files, not 14 days, so that noise cut the history to about 3.5 days.
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 50_000_000,
                rollOnFileSizeLimit: true,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));
    }
}
