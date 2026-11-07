// offline.html (shown by sw.js when a page can't be reached): load the page again once the
// connection is back. The address is still the page that failed, so a reload retries it.
'use strict';
window.addEventListener('online', () => location.reload());
