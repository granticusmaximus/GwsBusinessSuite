// The admin shell intentionally uses plain DOM/CSS so it remains available on both
// statically rendered and interactive Blazor pages. All queries resolve the current DOM,
// which also makes the controls survive enhanced-navigation page swaps.
(function () {
	var shellEventsBound = false;
	var commandIndex = -1;

	function isMobile() {
		return window.matchMedia('(max-width: 767.98px)').matches;
	}

	function shell() {
		return document.querySelector('.gws-layout');
	}

	function closeMobileNavigation() {
		var layout = shell();
		var toggle = document.querySelector('.gws-sidebar-toggle');
		var sidebar = document.querySelector('.gws-sidebar');
		var backdrop = document.querySelector('.gws-sidebar-backdrop');
		if (!layout) return;
		layout.classList.remove('gws-mobile-nav-open');
		document.body.classList.remove('gws-no-scroll');
		if (sidebar && isMobile()) {
			sidebar.inert = true;
			sidebar.setAttribute('aria-hidden', 'true');
		}
		if (backdrop) {
			backdrop.tabIndex = -1;
			backdrop.setAttribute('aria-hidden', 'true');
		}
		if (toggle && isMobile()) {
			toggle.setAttribute('aria-expanded', 'false');
			toggle.title = 'Open navigation';
			toggle.setAttribute('aria-label', 'Open navigation');
		}
	}

	function applySidebarState() {
		var layout = shell();
		var toggle = document.querySelector('.gws-sidebar-toggle');
		var sidebar = document.querySelector('.gws-sidebar');
		var backdrop = document.querySelector('.gws-sidebar-backdrop');
		if (!layout || !toggle) return;

		if (isMobile()) {
			layout.classList.remove('gws-sidebar-collapsed');
			var mobileOpen = layout.classList.contains('gws-mobile-nav-open');
			if (sidebar) {
				sidebar.inert = !mobileOpen;
				sidebar.setAttribute('aria-hidden', mobileOpen ? 'false' : 'true');
			}
			if (backdrop) {
				backdrop.tabIndex = mobileOpen ? 0 : -1;
				backdrop.setAttribute('aria-hidden', mobileOpen ? 'false' : 'true');
			}
			toggle.setAttribute('aria-expanded', mobileOpen ? 'true' : 'false');
			toggle.title = mobileOpen ? 'Close navigation' : 'Open navigation';
			toggle.setAttribute('aria-label', toggle.title);
			return;
		}

		closeMobileNavigation();
		if (sidebar) {
			sidebar.inert = false;
			sidebar.removeAttribute('aria-hidden');
		}
		var collapsed = localStorage.getItem('gws-sidebar-collapsed') === 'true';
		layout.classList.toggle('gws-sidebar-collapsed', collapsed);
		toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
		toggle.title = collapsed ? 'Expand sidebar' : 'Collapse sidebar';
		toggle.setAttribute('aria-label', toggle.title);
	}

	function toggleNavigation() {
		var layout = shell();
		if (!layout) return;
		if (isMobile()) {
			var opening = !layout.classList.contains('gws-mobile-nav-open');
			layout.classList.toggle('gws-mobile-nav-open', opening);
			document.body.classList.toggle('gws-no-scroll', opening);
			applySidebarState();
			if (opening) {
				window.setTimeout(function () {
					var first = document.querySelector('.gws-sidebar .gws-nav-link');
					if (first) first.focus();
				}, 80);
			}
			return;
		}

		var collapsed = layout.classList.toggle('gws-sidebar-collapsed');
		localStorage.setItem('gws-sidebar-collapsed', collapsed ? 'true' : 'false');
		applySidebarState();
	}

	function commandElements() {
		return {
			layer: document.querySelector('[data-gws-command-layer]'),
			input: document.querySelector('[data-gws-command-input]'),
			results: document.querySelector('[data-gws-command-results]')
		};
	}

	function commandEntries() {
		return Array.from(document.querySelectorAll('.gws-sidebar .gws-nav-link')).map(function (link) {
			var copy = link.querySelector('.gws-nav-link-copy');
			var title = copy && copy.firstElementChild ? copy.firstElementChild.textContent.trim() : link.textContent.trim();
			var description = copy && copy.querySelector('small') ? copy.querySelector('small').textContent.trim() : '';
			var group = link.closest('.gws-nav-group');
			var groupName = group && group.querySelector('summary span') ? group.querySelector('summary span').textContent.trim() : 'General';
			var icon = link.querySelector('i');
			return { title: title, description: description, group: groupName, href: link.href, iconClass: icon ? icon.className : 'bi bi-arrow-right' };
		});
	}

	function renderCommandResults(query) {
		var elements = commandElements();
		if (!elements.results) return;
		var normalized = (query || '').trim().toLowerCase();
		var entries = commandEntries().filter(function (entry) {
			return !normalized || [entry.title, entry.description, entry.group].join(' ').toLowerCase().includes(normalized);
		}).slice(0, 10);

		elements.results.replaceChildren();
		commandIndex = entries.length ? 0 : -1;
		entries.forEach(function (entry, index) {
			var link = document.createElement('a');
			link.className = 'gws-command-result' + (index === commandIndex ? ' is-selected' : '');
			link.href = entry.href;
			link.dataset.commandResult = '';
			link.innerHTML = '<i aria-hidden="true"></i><span><strong></strong><small></small></span><em></em>';
			link.querySelector('i').className = entry.iconClass;
			link.querySelector('strong').textContent = entry.title;
			link.querySelector('small').textContent = entry.description;
			link.querySelector('em').textContent = entry.group;
			elements.results.appendChild(link);
		});

		if (!entries.length) {
			var empty = document.createElement('div');
			empty.className = 'gws-command-empty';
			empty.textContent = 'No pages or tools match your search.';
			elements.results.appendChild(empty);
		}
	}

	function openCommand() {
		var elements = commandElements();
		if (!elements.layer || !elements.input) return;
		elements.layer.hidden = false;
		document.body.classList.add('gws-no-scroll');
		elements.input.value = '';
		renderCommandResults('');
		window.setTimeout(function () { elements.input.focus(); }, 0);
	}

	function closeCommand() {
		var elements = commandElements();
		if (!elements.layer || elements.layer.hidden) return;
		elements.layer.hidden = true;
		document.body.classList.remove('gws-no-scroll');
		var trigger = document.querySelector('[data-gws-command-open]');
		if (trigger) trigger.focus();
	}

	function moveCommandSelection(direction) {
		var results = Array.from(document.querySelectorAll('[data-command-result]'));
		if (!results.length) return;
		commandIndex = (commandIndex + direction + results.length) % results.length;
		results.forEach(function (result, index) {
			result.classList.toggle('is-selected', index === commandIndex);
		});
		results[commandIndex].scrollIntoView({ block: 'nearest' });
	}

	function bindShellEventsOnce() {
		if (shellEventsBound) return;
		shellEventsBound = true;

		document.addEventListener('click', function (event) {
			if (event.target.closest('.gws-sidebar-toggle')) toggleNavigation();
			if (event.target.closest('[data-gws-sidebar-close]')) closeMobileNavigation();
			if (event.target.closest('.gws-sidebar .gws-nav-link') && isMobile()) closeMobileNavigation();
			if (event.target.closest('[data-gws-command-open]')) openCommand();
			if (event.target.closest('[data-gws-command-close]')) closeCommand();
		});

		document.addEventListener('input', function (event) {
			if (event.target.matches('[data-gws-command-input]')) renderCommandResults(event.target.value);
		});

		document.addEventListener('keydown', function (event) {
			if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
				event.preventDefault();
				openCommand();
				return;
			}
			var elements = commandElements();
			var commandOpen = elements.layer && !elements.layer.hidden;
			if (event.key === 'Escape') {
				if (commandOpen) closeCommand(); else closeMobileNavigation();
			} else if (commandOpen && event.key === 'ArrowDown') {
				event.preventDefault();
				moveCommandSelection(1);
			} else if (commandOpen && event.key === 'ArrowUp') {
				event.preventDefault();
				moveCommandSelection(-1);
			} else if (commandOpen && event.key === 'Enter') {
				var selected = document.querySelector('[data-command-result].is-selected');
				if (selected) selected.click();
			}
		});

		window.addEventListener('resize', applySidebarState);
	}

	function initializeAdminShell() {
		bindShellEventsOnce();
		applySidebarState();
	}

	document.addEventListener('DOMContentLoaded', initializeAdminShell);
	document.addEventListener('blazor:enhancedload', initializeAdminShell);

	window.gwsTooltips = {
		init: function (selector) {
			if (!window.bootstrap || !window.bootstrap.Tooltip) return;
			document.querySelectorAll(selector || '[data-bs-toggle="tooltip"]').forEach(function (element) {
				var existing = window.bootstrap.Tooltip.getInstance(element);
				if (existing) existing.dispose();
				new window.bootstrap.Tooltip(element, { html: true, boundary: 'viewport' });
			});
		}
	};
})();
