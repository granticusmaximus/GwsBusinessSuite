// grantwatson.dev public-site interactivity: scroll-triggered section reveals and a
// soft cursor glow. Both are pure progressive enhancement - the reveal targets are only
// hidden by CSS when <html> already has .js-reveal, which the inline head script in
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
