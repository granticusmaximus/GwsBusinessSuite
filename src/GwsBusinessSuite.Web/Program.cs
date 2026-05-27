using GwsBusinessSuite.Infrastructure;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
var configuredPathBase = builder.Configuration["Hosting:PathBase"];

// Add services to the container.
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser().RequireRole("Admin"));

    options.FallbackPolicy = options.GetPolicy("AdminOnly");
});

var app = builder.Build();

var normalizedPathBase = NormalizePathBase(configuredPathBase);

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var dbContext = await dbFactory.CreateDbContextAsync();
    await dbContext.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

if (!string.IsNullOrWhiteSpace(normalizedPathBase))
{
    app.UsePathBase(normalizedPathBase);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/auth/login", async (HttpContext httpContext, IConfiguration configuration) =>
{
    var form = await httpContext.Request.ReadFormAsync();
    var username = form["username"].ToString().Trim();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    var configuredUsername = configuration["AdminAuth:Username"]?.Trim();
    var configuredPassword = configuration["AdminAuth:Password"];

    if (string.IsNullOrWhiteSpace(configuredUsername) || string.IsNullOrWhiteSpace(configuredPassword))
    {
        return Results.LocalRedirect("/login?error=missing");
    }

    if (!string.Equals(username, configuredUsername, StringComparison.Ordinal) ||
        !string.Equals(password, configuredPassword, StringComparison.Ordinal))
    {
        var safeReturnUrl = IsSafeLocalPath(returnUrl) ? returnUrl : "/";
        return Results.LocalRedirect($"/login?error=invalid&returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, configuredUsername),
        new(ClaimTypes.Role, "Admin")
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

    return Results.LocalRedirect(IsSafeLocalPath(returnUrl) ? returnUrl : "/");
}).AllowAnonymous();

app.MapGet("/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
}).AllowAnonymous();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string NormalizePathBase(string? pathBase)
{
    if (string.IsNullOrWhiteSpace(pathBase))
    {
        return string.Empty;
    }

    var normalized = pathBase.Trim();
    if (!normalized.StartsWith('/'))
    {
        normalized = $"/{normalized}";
    }

    return normalized.Length > 1
        ? normalized.TrimEnd('/')
        : normalized;
}

static bool IsSafeLocalPath(string? returnUrl)
{
    return !string.IsNullOrWhiteSpace(returnUrl)
           && returnUrl.StartsWith("/", StringComparison.Ordinal)
           && !returnUrl.StartsWith("//", StringComparison.Ordinal)
           && !returnUrl.Contains("\\", StringComparison.Ordinal);
}
