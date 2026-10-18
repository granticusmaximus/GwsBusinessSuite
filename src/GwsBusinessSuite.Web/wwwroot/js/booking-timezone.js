// The visitor's IANA time zone (e.g. "America/Chicago"), so the public booking page can show
// times as the visitor reads them. Empty when the browser won't say.
export function browserTimeZone() {
    try {
        return Intl.DateTimeFormat().resolvedOptions().timeZone || "";
    } catch {
        return "";
    }
}
