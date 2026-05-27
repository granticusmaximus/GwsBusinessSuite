# Admin Bridge Guide

This document explains how to bridge `GwsBusinessSuite` as an admin/backend suite for an existing public site without changing public frontend code.

## Goal

- Public site remains unchanged.
- Visiting `/admin` routes users to the Blazor admin app.

## What was added in this repo

`src/GwsBusinessSuite.Web/Program.cs` supports a configurable path base:

- `Hosting:PathBase` (for example `/admin`)

`src/GwsBusinessSuite.Web/appsettings.json` includes:

- `Hosting:PathBase`

## Recommended deployment topology

1. Deploy `GwsBusinessSuite.Web` to its own host, for example:
   - `admin.example.com` (recommended)
   - or another reachable host such as `your-admin-host.example.net`
2. Set environment variable on that host:
   - `Hosting__PathBase=/admin`
3. Ensure the host supports ASP.NET Core + SignalR/WebSockets (Blazor Server requirement).

## Frontend host bridge without code changes

Use your frontend host dashboard routing/redirects.

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
