using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GwsBusinessSuite.Tests;

public sealed class WebStartupProbeTests
{
    [Fact]
    public async Task SharedHostBootstrapAndMigrationPathCompletes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gws-startup-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dbPath = Path.Combine(tempDir, "probe.db");
            var keysPath = Path.Combine(tempDir, "dp-keys");
            Directory.CreateDirectory(keysPath);

            var builder = WebApplication.CreateBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={dbPath}",
                ["DataProtection:KeysPath"] = keysPath
            });

            builder.Services.AddInfrastructure(builder.Configuration);
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

            var app = builder.Build();

            using var scope = app.Services.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var dbContext = await dbFactory.CreateDbContextAsync();

            await dbContext.Database.MigrateAsync();
            var canConnect = await dbContext.Database.CanConnectAsync();

            canConnect.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MapRazorComponentsWithSimpleRootCompletesPromptly()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();
        var completed = await Task.Run(() =>
        {
            app.MapRazorComponents<ProbeRootComponent>()
                .AddInteractiveServerRenderMode();
            return true;
        }).WaitAsync(TimeSpan.FromSeconds(5));

        completed.Should().BeTrue();
    }

    [Fact]
    public async Task MinimalHostWithInfrastructureStartsAndStopsPromptly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gws-startup-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dbPath = Path.Combine(tempDir, "probe.db");
            var keysPath = Path.Combine(tempDir, "dp-keys");
            Directory.CreateDirectory(keysPath);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={dbPath}",
                ["DataProtection:KeysPath"] = keysPath
            });

            builder.Services.AddInfrastructure(builder.Configuration);
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

            var app = builder.Build();
            app.MapGet("/", () => "ok");

            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class ProbeRootComponent : IComponent
    {
        public void Attach(RenderHandle renderHandle)
        {
        }

        public Task SetParametersAsync(ParameterView parameters) => Task.CompletedTask;
    }
}
