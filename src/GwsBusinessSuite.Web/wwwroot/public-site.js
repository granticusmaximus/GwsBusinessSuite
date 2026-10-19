// grantwatson.dev public-site interactivity: scroll-triggered section reveals and a
// soft cursor glow. Both are pure progressive enhancement - the reveal targets are only
// hidden by CSS when <html> already has .js-reveal, which the head script (js/reveal-init.js) in
// PublicSiteHtmlRenderer.Layout only adds when prefers-reduced-motion is off, so
// no-JS and reduced-motion visitors always see full content with no animation.
(function () {
  var root = document.documentElement;
  var revealSelector = '.gws-section, .resume-card, .blog-page-header, .page-not-found';

  if (root.classList.contains('js-reveal')) {
    var targets = document.querySelectorAll(revealSelector);

    if ('IntersectionObserver' in window) {
      var observer = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            entry.target.classList.add('is-revealed');
            observer.unobserve(entry.target);
          }
        });
      }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });

      targets.forEach(function (el) { observer.observe(el); });
    } else {
      targets.forEach(function (el) { el.classList.add('is-revealed'); });
    }
  }

  // Soft cursor glow - fine-pointer (mouse/trackpad) devices only, skipped for
  // reduced-motion visitors same as the reveal system above.
  var hasFinePointer = window.matchMedia('(pointer: fine)').matches;
  var reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  if (hasFinePointer && !reducedMotion) {
    var glow = document.createElement('div');
    glow.className = 'cursor-glow';
    document.body.appendChild(glow);

    var targetX = window.innerWidth / 2;
    var targetY = window.innerHeight / 2;
    var currentX = targetX;
    var currentY = targetY;

    document.addEventListener('mousemove', function (e) {
      targetX = e.clientX;
      targetY = e.clientY;
      glow.classList.add('is-visible');
    });

    (function animate() {
      currentX += (targetX - currentX) * 0.16;
      currentY += (targetY - currentY) * 0.16;
      glow.style.transform = 'translate3d(' + currentX + 'px,' + currentY + 'px,0)';
      requestAnimationFrame(animate);
    }());

    var hoverSelector = 'a, button, .btn, [role="button"]';
    document.addEventListener('mouseover', function (e) {
      if (e.target.closest && e.target.closest(hoverSelector)) glow.classList.add('is-active');
    });
    document.addEventListener('mouseout', function (e) {
      if (e.target.closest && e.target.closest(hoverSelector)) glow.classList.remove('is-active');
    });
  }
}());

// Menu dropdowns (a header item with sub-links): tap/click toggles it, a click elsewhere closes
// it, and Escape closes it (even while hovered or focused) and returns focus to its button.
(function () {
  function closeAll(except) {
    document.querySelectorAll('.nav-dropdown.is-open').forEach(function (dropdown) {
      if (dropdown === except) return;
      dropdown.classList.remove('is-open');
      var button = dropdown.querySelector('[data-gws-dropdown]');
      if (button) button.setAttribute('aria-expanded', 'false');
    });
  }
  document.addEventListener('click', function (e) {
    var toggle = e.target.closest && e.target.closest('[data-gws-dropdown]');
    if (toggle) {
      var dropdown = toggle.closest('.nav-dropdown');
      var open = !dropdown.classList.contains('is-open');
      closeAll(dropdown);
      dropdown.classList.toggle('is-open', open);
      dropdown.classList.remove('is-dismissed');
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      return;
    }
    if (!(e.target.closest && e.target.closest('.nav-dropdown'))) closeAll(null);
  });
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;
    var active = document.activeElement && document.activeElement.closest && document.activeElement.closest('.nav-dropdown');
    document.querySelectorAll('.nav-dropdown.is-open, .nav-dropdown:hover').forEach(function (dropdown) { dropdown.classList.add('is-dismissed'); });
    if (active) {
      active.classList.add('is-dismissed');
      var button = active.querySelector('[data-gws-dropdown]');
      if (button) button.focus();
    }
    closeAll(null);
  });
  // A dismissed dropdown can open again once the pointer or focus has left it.
  ['mouseout', 'focusout'].forEach(function (type) {
    document.addEventListener(type, function (e) {
      var dropdown = e.target.closest && e.target.closest('.nav-dropdown.is-dismissed');
      if (dropdown && !dropdown.contains(e.relatedTarget)) dropdown.classList.remove('is-dismissed');
    });
  });
}());

// Phone menu buttons (built-in header and custom-header Nav Menus): toggle the menu named by
// aria-controls, keep aria-expanded in sync, and close on Escape or after following a link.
(function () {
  document.addEventListener('click', function (e) {
    var toggle = e.target.closest && e.target.closest('[data-gws-menu-toggle]');
    if (toggle) {
      var menu = document.getElementById(toggle.getAttribute('aria-controls'));
      if (!menu) return;
      var open = !menu.classList.contains('is-open');
      menu.classList.toggle('is-open', open);
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      return;
    }
    if (e.target.closest && e.target.closest('.nav-links.is-open a, .gws-nav-menu.is-open a')) {
      document.querySelectorAll('[data-gws-menu-toggle][aria-expanded="true"]').forEach(function (b) { b.click(); });
    }
  });
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;
    document.querySelectorAll('[data-gws-menu-toggle][aria-expanded="true"]').forEach(function (b) { b.click(); b.focus(); });
  });
}());
