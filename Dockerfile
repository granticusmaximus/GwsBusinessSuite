# ─────────────────────────────────────────────
# Stage 1: Restore and build the .NET Blazor app
# ─────────────────────────────────────────────
# Layer order is for build caching (deploys build on the droplet, which keeps Docker's build
# cache between deploys):
# - Only the Web app and the three projects it references are restored and published - never
#   the whole solution. So the image needs no maui-android workload: that's for the native app,
#   which this image doesn't contain.
# - The project files are copied and restored before any source, so an ordinary code change
#   reuses the restore layer instead of downloading every package again.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY Directory.Build.props ./
COPY src/GwsBusinessSuite.Domain/GwsBusinessSuite.Domain.csproj src/GwsBusinessSuite.Domain/
COPY src/GwsBusinessSuite.Application/GwsBusinessSuite.Application.csproj src/GwsBusinessSuite.Application/
COPY src/GwsBusinessSuite.Infrastructure/GwsBusinessSuite.Infrastructure.csproj src/GwsBusinessSuite.Infrastructure/
COPY src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj src/GwsBusinessSuite.Web/
RUN dotnet restore src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj

FROM restore AS dotnet-build
COPY src/GwsBusinessSuite.Domain/ src/GwsBusinessSuite.Domain/
COPY src/GwsBusinessSuite.Application/ src/GwsBusinessSuite.Application/
COPY src/GwsBusinessSuite.Infrastructure/ src/GwsBusinessSuite.Infrastructure/
COPY src/GwsBusinessSuite.Web/ src/GwsBusinessSuite.Web/
RUN dotnet publish src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj \
    -c Release -o /app/publish --no-restore \
    && grep -q '"_framework/blazor.web' /app/publish/GwsBusinessSuite.Web.staticwebassets.endpoints.json \
    || { echo "Publish has no _framework/blazor.web.js - every button would be dead. See RequiresAspNetWebAssets in GwsBusinessSuite.Web.csproj." >&2; exit 1; }

# ─────────────────────────────────────────────
# Stage 2: Final runtime image
# ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    jq \
    libfontconfig1 \
    fonts-liberation \
    libssl3 \
    ca-certificates \
    # Lets TZ (docker-compose.yml) resolve, so admin pages show times in Grant's zone, not UTC.
    tzdata \
    # --- Headless Chromium runtime deps for Microsoft.Playwright (LocalEventsScraperService) ---
    libglib2.0-0t64 \
    libnss3 \
    libatk1.0-0t64 \
    libatk-bridge2.0-0t64 \
    libcups2t64 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libasound2t64 \
    libpango-1.0-0 \
    libpangocairo-1.0-0 \
    && rm -rf /var/lib/apt/lists/*

# Fixed path so the build-time install below and the running container agree on where
# the Chromium binary lives, regardless of $HOME/which user the container runs as.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

# Bakes the Chromium binary into the image (LocalEventsScraperService). It runs through the
# Microsoft.Playwright package's own bundled driver (node + cli.js) - the same thing
# `dotnet GwsBusinessSuite.Web.dll --install-playwright-browsers` calls - taken from the restore
# stage rather than the published app. So this layer only changes when the Playwright package
# version does, instead of re-downloading Chromium on every code change. The base image has no
# PowerShell for playwright.ps1; the system libraries above come from apt instead.
COPY --from=restore /root/.nuget/packages/microsoft.playwright /tmp/playwright-package
RUN driver="$(ls -d /tmp/playwright-package/*/.playwright | head -1)" \
    && case "$(uname -m)" in aarch64|arm64) platform=linux-arm64 ;; *) platform=linux-x64 ;; esac \
    && "$driver/node/$platform/node" "$driver/package/cli.js" install chromium \
    && rm -rf /tmp/playwright-package

COPY scripts/measure-sentinelgpt-latency.sh /usr/local/bin/measure-sentinelgpt-latency
RUN chmod 0755 /usr/local/bin/measure-sentinelgpt-latency
COPY --from=dotnet-build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "GwsBusinessSuite.Web.dll"]
