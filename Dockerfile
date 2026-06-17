# ─────────────────────────────────────────────
# Stage 1: Build the React public site
# ─────────────────────────────────────────────
FROM node:20-alpine AS react-build
WORKDIR /app
COPY apps/public-site/package*.json ./
RUN npm ci
COPY apps/public-site/ .
RUN npm run build -- --outDir dist --emptyOutDir

# ─────────────────────────────────────────────
# Stage 2: Build the .NET Blazor app
# ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY . .
RUN dotnet restore GwsBusinessSuite.slnx
RUN dotnet publish src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj \
    -c Release -o /app/publish --no-restore

# ─────────────────────────────────────────────
# Stage 3: Final runtime image
# ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Copy published .NET app
COPY --from=dotnet-build /app/publish .

# Overlay React build into wwwroot so ASP.NET Core serves it as static files
COPY --from=react-build /app/dist ./wwwroot/

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "GwsBusinessSuite.Web.dll"]
