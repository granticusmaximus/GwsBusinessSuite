// Netlify's redirect-based reverse proxy for /api/* and /og-image/* (see netlify.toml
// history) was unreliable in practice - it intermittently failed at the edge instead of
// forwarding to the DigitalOcean backend. Calling the backend's absolute URL directly
// (with CORS enabled there for this site's origin) is more robust.
export const API_BASE_URL = import.meta.env.DEV
  ? (import.meta.env.VITE_BACKEND_URL || 'http://localhost:5050')
  : 'https://admin.gwsapp.net';

// Backend responses contain relative paths (e.g. heroImageUrl: "/og-image/slug") meant
// to be served from the backend host. Resolve them against API_BASE_URL.
export function resolveBackendUrl(path) {
  if (!path) return path;
  if (/^https?:\/\//i.test(path)) return path;
  return `${API_BASE_URL}${path}`;
}
