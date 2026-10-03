// Close behaviour for the "Thanks for your submission" modal (PublicSiteHtmlRenderer.SubmittedModal).
// External because an inline script never runs under the site's CSP.
(function () {
  var modal = document.getElementById('gws-submitted-modal');
  var closeBtn = document.getElementById('gws-submitted-modal-close');
  if (!modal || !closeBtn) return;

  function close() {
    modal.style.display = 'none';
    if (window.history && window.history.replaceState) {
      var url = new URL(window.location.href);
      url.searchParams.delete('submitted');
      window.history.replaceState({}, document.title, url.pathname + url.search + url.hash);
    }
  }

  closeBtn.addEventListener('click', close);
  modal.addEventListener('click', function (e) { if (e.target === modal) close(); });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });
  closeBtn.focus();
})();
