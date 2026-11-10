using GwsBusinessSuite.Application.Overwatch;

namespace GwsBusinessSuite.Web;

/// <summary>
/// Discovery endpoint for the embedded native Overwatch renderer. Viewport retrieval will use the
/// same authenticated session as this endpoint; the existing web renderer remains independent.
/// </summary>
public static class NativeOverwatchEndpointExtensions
{
    public const string ContractVersion = "2026-11-10";

    public static WebApplication MapNativeOverwatchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/native-overwatch/contract", () => Results.Ok(new
            {
                version = ContractVersion,
                operations = NativeOverwatchOperations.All,
                request = new
                {
                    bounds = new[] { "north", "south", "east", "west" },
                    controls = typeof(NativeOverwatchControls).GetProperties().Select(property => property.Name)
                }
            }))
            .WithTags("Native Overwatch")
            .RequireAuthorization("AdminOnly");

        return app;
    }
}
