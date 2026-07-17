# ─────────────────────────────────────────────
# Stage 1: Build the .NET Blazor app
# ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY . .
RUN dotnet restore GwsBusinessSuite.slnx
RUN dotnet publish src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj \
    -c Release -o /app/publish --no-restore

# ─────────────────────────────────────────────
# Stage 2: Final runtime image
# ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    libfontconfig1 \
    fonts-liberation \
    libssl3 \
    ca-certificates \
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

COPY --from=dotnet-build /app/publish .

# Fixed path so the build-time install below and the running container agree on where
# the Chromium binary lives, regardless of $HOME/which user the container runs as.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

# Bakes the Chromium binary into this image layer at build time. This base image has no
# PowerShell, so the usual `playwright.ps1 install --with-deps chromium` script can't run
# here - the system libraries above are installed via apt instead, and this just downloads
# the browser itself through the driver's own cross-platform entrypoint (see the
# --install-playwright-browsers hook at the top of Program.cs).
RUN dotnet GwsBusinessSuite.Web.dll --install-playwright-browsers

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "GwsBusinessSuite.Web.dll"]
