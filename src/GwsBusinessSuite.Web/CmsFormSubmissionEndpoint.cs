using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Web;

public static class CmsFormSubmissionEndpoint
{
    public static async Task<IResult> HandleAsync(
        string siteSlug, HttpRequest request, HttpContext httpContext,
        ICmsBuilderService cmsBuilderService, IFormSubmissionService formSubmissionService,
        TurnstileService turnstile, IConfiguration configuration, bool isPublicHost)
    {
        var site = await cmsBuilderService.GetSiteBySlugAsync(siteSlug);
        if (site is null) return Results.NotFound();

        var form = await request.ReadFormAsync();
        var path = form["_path"].ToString();

        // Lets an authenticated admin test a draft page's form from the Studio preview; an
        // anonymous visitor can never reach that far since the draft page itself already 404s
        // for them before a form could be submitted.
        var includeUnpublished = httpContext.User.Identity?.IsAuthenticated == true;
        var page = await cmsBuilderService.GetPageByFullPathAsync(site.Id, path, includeUnpublished);
        if (page is null) return Results.NotFound();

        // The same form widget renders on both the bare /cms/ fallback and the real public
        // site (RequireHost-gated routes below) — send visitors back to whichever one they
        // came from instead of always landing on the unstyled fallback.
        var isContactPage = ContactFormProtection.AppliesTo(site, page, configuration);
        var thanksUrl = isPublicHost ? $"/{path}?submitted=1" : $"/cms/{siteSlug}/{path}";

        if (isContactPage)
            thanksUrl = isPublicHost ? "/contact/thank-you" : $"/cms/{Uri.EscapeDataString(site.Slug)}/contact/thank-you";

        // Honeypot: a hidden field real visitors never see or fill. A non-empty value means a
        // bot filled every field it found — accept silently so the bot doesn't learn it failed.
        // "_hp", not a plain word, so it can never collide with an admin-labeled field's own
        // derived key (see CmsBlockHtmlRenderer's comment on this input for why that happened).
        if (!string.IsNullOrWhiteSpace(form["_hp"]))
        {
            return Success(request, thanksUrl);
        }

        if (isContactPage)
        {
            var verification = await turnstile.VerifyAsync(form[TurnstileService.ResponseField].ToString(),
                request.Host.Host, httpContext.Connection.RemoteIpAddress?.ToString(), httpContext.RequestAborted);
            if (verification != TurnstileResult.Verified)
            {
                var unavailable = verification == TurnstileResult.Unavailable;
                var message = unavailable
                    ? "Verification is temporarily unavailable. Your message has not been sent. Please try again shortly."
                    : "Please complete the verification again, then send your message.";
                var statusCode = unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status400BadRequest;
                if (WantsJson(request)) return Results.Json(new { error = message }, statusCode: statusCode);
                return Results.Content(PublicSiteHtmlRenderer.Layout("Unable to send message", string.Empty, null,
                    $"<main class=\"page-not-found\"><h1>Unable to send your message</h1><p>{message}</p><p>Use your browser’s Back button to return to the form and try again.</p></main>",
                    PublicSiteHtmlRenderer.ParseNavItems(site.NavMenuJson),
                    PublicSiteHtmlRenderer.ParseFooterNavItems(site.FooterNavMenuJson),
                    site.AccentColorHex, site.FontPairingKey, siteName: site.Name,
                    logoUrl: site.LogoUrl, faviconUrl: site.FaviconUrl,
                    tokens: DesignTokenJson.ParseOrEmpty(site.DesignTokensJson)),
                    "text/html", statusCode: statusCode);
            }
        }

        // The form widget's fields are admin-defined per page, so collect whatever was
        // actually posted (minus the honeypot and the routing field) rather than assuming
        // fixed field names. Stored keyed by the field's configured display Label (resolved from
        // the page's live BlocksJson) rather than the raw posted key/HTML "name" attribute, so a
        // submission reads as "Full Name: Ada" instead of "fullName: Ada" wherever it's later
        // shown (admin detail page, notification email). Falls back to the raw key for a field
        // the resolver can't find (e.g. the widget was edited/removed after this submission).
        var metadata = ResolveFormFieldMetadata(page.BlocksJson);
        var fields = form
            .Where(kvp => kvp.Key != "_hp" && kvp.Key != "_path" && kvp.Key != TurnstileService.ResponseField)
            .ToDictionary(kvp => metadata.LabelsByKey.GetValueOrDefault(kvp.Key, kvp.Key), kvp => kvp.Value.ToString());

        // Roles are keyed by the field's raw posted key/name (same as labels), not its display
        // label - resolve role -> submitted value directly from the posted form, not from `fields`
        // above (which is already relabeled and would need a second lookup either way).
        var identityFields = new Dictionary<string, string>();
        foreach (var (key, role) in metadata.RoleByKey)
        {
            var value = form[key].ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                identityFields[role] = value;
            }
        }

        try
        {
            await formSubmissionService.SubmitAsync(page.Id, fields, identityFields, metadata.AutoCreateContact, httpContext.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        return Success(request, thanksUrl);
    }

    private static bool WantsJson(HttpRequest request) =>
        request.GetTypedHeaders().Accept?.Any(value => value.MediaType == "application/json") == true;

    private static IResult Success(HttpRequest request, string redirectUrl) =>
        WantsJson(request) ? Results.Json(new { redirectUrl }) : Results.Redirect(redirectUrl);

    private static FormFieldMetadata ResolveFormFieldMetadata(string blocksJson)
    {
        var labelsByKey = new Dictionary<string, string>();
        var roleByKey = new Dictionary<string, string>();
        var autoCreateContact = false;
        var layout = CmsBuilderJson.ParseLayout(blocksJson);
        if (layout is null) return new(labelsByKey, roleByKey, autoCreateContact);

        foreach (var widget in layout.Sections.SelectMany(s => s.Columns).SelectMany(c => c.Widgets))
        {
            if (widget.WidgetType != "form" || !widget.Props.TryGetValue("fieldsJson", out var fieldsJson))
            {
                continue;
            }

            if (widget.Props.TryGetValue("autoCreateContact", out var autoCreateRaw)
                && bool.TryParse(autoCreateRaw, out var widgetAutoCreate) && widgetAutoCreate)
            {
                autoCreateContact = true;
            }

            try
            {
                var array = System.Text.Json.Nodes.JsonNode.Parse(
                    string.IsNullOrWhiteSpace(fieldsJson) ? "[]" : fieldsJson) as System.Text.Json.Nodes.JsonArray;
                if (array is null) continue;

                foreach (var item in array.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    var key = item["key"]?.GetValue<string>();
                    var label = item["label"]?.GetValue<string>();
                    var role = item["role"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(label))
                    {
                        labelsByKey[key] = label;
                    }
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(role) && role != "none")
                    {
                        roleByKey[key] = role;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed fieldsJson on this widget - callers fall back to the raw posted key.
            }
        }

        return new(labelsByKey, roleByKey, autoCreateContact);
    }


    private sealed record FormFieldMetadata(
        Dictionary<string, string> LabelsByKey, Dictionary<string, string> RoleByKey, bool AutoCreateContact);
}
