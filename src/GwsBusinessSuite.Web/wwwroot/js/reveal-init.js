// Loaded synchronously in <head> (an inline script can't run under the site's CSP): marks
// <html> with .js-reveal before first paint so public-site.js can animate sections in. Skipped
// for reduced-motion visitors, who always see full content immediately.
if (!window.matchMedia || !window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
  document.documentElement.classList.add('js-reveal');
}
