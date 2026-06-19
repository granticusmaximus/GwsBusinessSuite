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
    && rm -rf /var/lib/apt/lists/*

COPY --from=dotnet-build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "GwsBusinessSuite.Web.dll"]
