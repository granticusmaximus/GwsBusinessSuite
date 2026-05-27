# Admin Bridge Guide

This document explains how to bridge `GwsBusinessSuite` as an admin/backend suite for an existing public site without changing public frontend code.

The React public site now includes a client-side `/admin` route that forwards users to the Blazor admin workspace. By default it targets `https://admin.grantwatson.dev/admin`, and you can override that with `VITE_ADMIN_APP_BASE_URL` in the React build environment.

## Goal

- Public site remains unchanged.
- Visiting `/admin` routes users to the Blazor admin app.

## What was added in this repo

`src/GwsBusinessSuite.Web/Program.cs` supports a configurable path base:

- `Hosting:PathBase` (for example `/admin`)

`src/GwsBusinessSuite.Web/appsettings.json` includes:

- `Hosting:PathBase`

`apps/public-site/src/App.jsx` and `apps/public-site/src/pages/AdminRedirect.jsx` now handle `/admin` in the React app and forward to the Blazor host.

## Recommended deployment topology

1. Deploy `GwsBusinessSuite.Web` to its own host, for example:
   - `admin.example.com` (recommended)
   - or another reachable host such as `your-admin-host.example.net`
2. Set environment variable on that host:
   - `Hosting__PathBase=/admin`
3. Ensure the host supports ASP.NET Core + SignalR/WebSockets (Blazor Server requirement).

## Frontend host bridge without code changes

Use your frontend host dashboard routing/redirects.

If you want the browser to jump straight from Netlify to the admin host instead of loading the React redirect page first, add a host-level redirect for `/admin` and `/admin/*` in Netlify later.

### Option A (most reliable): redirect

Create a redirect rule so `/admin` hands off to the admin host:

- From: `/admin/*`
- To: `https://admin.example.com/admin/:splat`
- Type: `301` (or `302` while testing)

This changes the browser URL to the admin host and avoids most proxy/WebSocket edge cases.

### Option B (advanced): reverse proxy rewrite

If your hosting plan/runtime supports stable proxying for WebSockets/SignalR, use a rewrite/proxy rule instead:

- From: `/admin/*`
- To: `https://admin.example.com/admin/:splat`
- Type: `200` rewrite/proxy

This can preserve `/admin/...` in the browser URL, but requires extra validation for Blazor Server circuit connectivity.

## Validation checklist

1. Open `/admin` from your public site domain.
2. Verify admin app loads and navigation works.
3. Verify long-running actions (draft generation, image regeneration) work.
4. Confirm no SignalR/WebSocket disconnect loop in browser console.

## Notes

- This setup does not modify the public frontend codebase.
- If later you want SSO/session sharing between public site and admin, add explicit auth integration and cookie/domain strategy.
