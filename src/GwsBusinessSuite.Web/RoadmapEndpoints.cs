using System.Security.Cryptography;
using System.Text;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.AspNetCore.Antiforgery;

namespace GwsBusinessSuite.Web;

// Votes on a board's public roadmap (Roadmap.razor, Boards K8e). The page is static, so a vote
// is a form post here: antiforgery, the public-write rate limit, then the vote - or taking it back.
// A visitor is a random id in a cookie, stored only as a hash with the share's id, so a browser
// votes once per card and nobody's identity or IP is kept.
public static class RoadmapEndpoints
{
    public const string VoterCookie = "gws_rv";

    public static void Map(WebApplication app)
    {
        app.MapPost("/roadmap/{token}/vote/{rowId:guid}", VoteAsync)
            .AllowAnonymous().RequireRateLimiting("public-write");
    }

    public static string VoterHash(Guid shareId, string voter) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{shareId:N}:{voter}"))).ToLowerInvariant();

    private static async Task<IResult> VoteAsync(
        string token,
        Guid rowId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ISentinelAccessService access,
        IKanbanBoardService boards)
    {
        var back = $"/roadmap/{token}";
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.LocalRedirect(back + "?voted=expired");
        }
        if (await access.ResolvePublicShareAsync(token) is not { IsRoadmap: true } share) return Results.LocalRedirect(back);

        var voter = httpContext.Request.Cookies[VoterCookie];
        if (string.IsNullOrWhiteSpace(voter) || voter.Length != 32)
        {
            voter = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            httpContext.Response.Cookies.Append(VoterCookie, voter, new CookieOptions
            {
                HttpOnly = true,
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/roadmap",
                MaxAge = TimeSpan.FromDays(365)
            });
        }
        var hash = VoterHash(share.Id, voter);
        var before = await boards.GetPublicRoadmapAsync(token, hash);
        var had = before?.Columns.SelectMany(column => column.Cards).FirstOrDefault(card => card.RowId == rowId)?.Voted ?? false;
        var count = await boards.VoteAsync(token, rowId, hash);
        var outcome = count is null ? "expired" : had ? "removed" : "added";
        return Results.LocalRedirect($"{back}?voted={outcome}#card-{rowId}");
    }
}
