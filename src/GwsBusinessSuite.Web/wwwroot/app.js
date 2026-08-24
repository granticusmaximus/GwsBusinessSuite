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

	function appendResultEntries(container, entries) {
		entries.forEach(function (entry) {
			var link = document.createElement('a');
			var index = document.querySelectorAll('[data-command-result]').length;
			link.className = 'gws-command-result' + (index === commandIndex ? ' is-selected' : '');
			link.href = entry.href;
			link.dataset.commandResult = '';
			link.innerHTML = '<i aria-hidden="true"></i><span><strong></strong><small></small></span><em></em>';
			link.querySelector('i').className = entry.iconClass;
			link.querySelector('strong').textContent = entry.title;
			link.querySelector('small').textContent = entry.description;
			link.querySelector('em').textContent = entry.group;
			container.appendChild(link);
		});
	}

	function clearCommandEmptyState() {
		var elements = commandElements();
		if (!elements.results) return;
		var empty = elements.results.querySelector('.gws-command-empty');
		if (empty) empty.remove();
	}

	function showCommandEmptyStateIfNoResults() {
		var elements = commandElements();
		if (!elements.results || elements.results.querySelector('[data-command-result]')) return;
		if (elements.results.querySelector('.gws-command-empty')) return;
		var empty = document.createElement('div');
		empty.className = 'gws-command-empty';
		empty.textContent = 'No pages, tools, or records match your search.';
		elements.results.appendChild(empty);
	}

	// Nav entries (page/tool names) render instantly from the already-loaded DOM. Live records
	// (CRM contacts/deals, Sentinel pages/databases, workflows, CMS pages, articles, affiliate
	// offers) come from a separate, debounced /admin/api/suite-search fetch appended below them
	// once it resolves - see fetchSuiteSearchResults. Keeping these two independent means the
	// palette never feels laggy waiting on a network round trip just to jump to a known page.
	var suiteSearchAbortController = null;
	var suiteSearchDebounceTimer = null;

	function renderCommandResults(query) {
		var elements = commandElements();
		if (!elements.results) return;
		var normalized = (query || '').trim().toLowerCase();
		var entries = commandEntries().filter(function (entry) {
			return !normalized || [entry.title, entry.description, entry.group].join(' ').toLowerCase().includes(normalized);
		}).slice(0, 10);

		elements.results.replaceChildren();
		commandIndex = entries.length ? 0 : -1;
		appendResultEntries(elements.results, entries);
		showCommandEmptyStateIfNoResults();
		scheduleSuiteSearch(query);
	}

	function scheduleSuiteSearch(query) {
		window.clearTimeout(suiteSearchDebounceTimer);
		if (suiteSearchAbortController) suiteSearchAbortController.abort();
		var normalized = (query || '').trim();
		if (normalized.length < 2) return;
		suiteSearchDebounceTimer = window.setTimeout(function () { fetchSuiteSearchResults(normalized); }, 200);
	}

	function fetchSuiteSearchResults(query) {
		var elements = commandElements();
		if (!elements.results) return;
		suiteSearchAbortController = new AbortController();
		fetch('/admin/api/suite-search?q=' + encodeURIComponent(query), { signal: suiteSearchAbortController.signal, headers: { Accept: 'application/json' } })
			.then(function (response) { return response.ok ? response.json() : []; })
			.then(function (records) {
				// The palette may have been closed, or the query may have changed, while this
				// request was in flight - elements.input still reflects the live input value,
				// so a stale response for an old query is silently dropped rather than appended.
				if (!elements.input || elements.input.value.trim() !== query) return;
				clearCommandEmptyState();
				appendResultEntries(elements.results, (records || []).map(function (record) {
					return { title: record.title, description: record.subtitle, group: record.category, href: record.url, iconClass: record.iconClass };
				}));
				if (commandIndex === -1 && elements.results.querySelector('[data-command-result]')) {
					commandIndex = 0;
					elements.results.querySelector('[data-command-result]').classList.add('is-selected');
				}
				showCommandEmptyStateIfNoResults();
			})
			.catch(function () { /* aborted or offline - nav-entry results already shown */ });
	}

	function openCommand() {
		// Inside Sentinel, Ctrl/Cmd+K should search Sentinel's own pages/blocks/database
		// rows (the same ranked search Ctrl/Cmd+Shift+F already opens - see
		// sentinel-workspace.js's shortcutHandler) rather than this generic palette, which
		// only ever indexes the static app-wide nav sidebar (commandEntries() below) and so
		// has nothing to do with the workspace content a Sentinel user is actually in.
		if (document.querySelector('.sentinel-workspace')) {
			document.querySelector('.sentinel-global-search')?.click();
			return;
		}
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

	// Makes every modal in the admin portal draggable by its header, with zero per-modal
	// wiring: every modal here is hand-rolled Bootstrap-flavored markup
	// (.modal.d-block > .modal-dialog > .modal-content > .modal-header, no Bootstrap JS - see
	// ConfirmModal.razor and every page's own modal markup), and that structure is consistent
	// enough app-wide that one delegated listener on document covers all of them, including
	// modals that don't exist yet at page-load time (Blazor renders them in/out via @if).
	var draggableModalsBound = false;
	var activeModalDrag = null; // { dialog, pointerId, startX, startY, baseLeft, baseTop }

	function isInteractiveTarget(element) {
		return !!(element instanceof Element && element.closest('button, a, input, select, textarea, [role="button"]'));
	}

	function bindDraggableModalsOnce() {
		if (draggableModalsBound) return;
		draggableModalsBound = true;

		document.addEventListener('pointerdown', function (event) {
			if (event.pointerType === 'mouse' && event.button !== 0) return;
			if (isInteractiveTarget(event.target)) return;
			var header = event.target instanceof Element ? event.target.closest('.modal-header') : null;
			if (!header) return;
			var dialog = header.closest('.modal-dialog');
			if (!dialog) return;

			var rect = dialog.getBoundingClientRect();
			activeModalDrag = {
				dialog: dialog,
				pointerId: event.pointerId,
				startX: event.clientX,
				startY: event.clientY,
				baseLeft: rect.left,
				baseTop: rect.top
			};
			// Freezes the dialog at its current rendered position (which may have come from
			// Bootstrap's flexbox centering, .modal-dialog-centered, etc.) with an explicit
			// fixed left/top, then every subsequent frame only adjusts that offset - this
			// works identically regardless of which modal-dialog variant classes are present,
			// without fighting their own centering logic.
			dialog.style.position = 'fixed';
			dialog.style.left = rect.left + 'px';
			dialog.style.top = rect.top + 'px';
			dialog.style.margin = '0';
			dialog.classList.add('gws-modal-dragging');
			document.body.classList.add('gws-modal-dragging-active');
			if (header.setPointerCapture) header.setPointerCapture(event.pointerId);
			event.preventDefault();
		});

		document.addEventListener('pointermove', function (event) {
			if (!activeModalDrag || event.pointerId !== activeModalDrag.pointerId) return;
			var dialog = activeModalDrag.dialog;
			var minVisible = 60; // keeps at least a corner grabbable so a modal can never be dragged fully unrecoverable off-screen
			var newLeft = activeModalDrag.baseLeft + (event.clientX - activeModalDrag.startX);
			var newTop = activeModalDrag.baseTop + (event.clientY - activeModalDrag.startY);
			newLeft = Math.max(minVisible - dialog.offsetWidth, Math.min(newLeft, window.innerWidth - minVisible));
			newTop = Math.max(0, Math.min(newTop, window.innerHeight - minVisible));
			dialog.style.left = newLeft + 'px';
			dialog.style.top = newTop + 'px';
		});

		function endModalDrag(event) {
			if (!activeModalDrag || (event.pointerId !== undefined && event.pointerId !== activeModalDrag.pointerId)) return;
			activeModalDrag.dialog.classList.remove('gws-modal-dragging');
			document.body.classList.remove('gws-modal-dragging-active');
			activeModalDrag = null;
		}

		document.addEventListener('pointerup', endModalDrag);
		document.addEventListener('pointercancel', endModalDrag);
	}

	function initializeAdminShell() {
		bindShellEventsOnce();
		bindDraggableModalsOnce();
		applySidebarState();
	}

	// Quick Note uses browser-local pointer movement instead of sending each mousemove over
	// the Blazor Server circuit. Pointer capture keeps the drag continuous outside the header,
	// while the body class prevents accidental text selection on the dashboard underneath.
	var quickNoteCleanups = {};
	window.gwsQuickNote = {
		init: function (elementId) {
			if (quickNoteCleanups[elementId]) return;
			var modal = document.getElementById(elementId);
			var handle = modal && modal.querySelector('[data-qn-drag-handle]');
			if (!modal || !handle) return;

			var drag = null;
			function pointerDown(event) {
				if (event.pointerType === 'mouse' && event.button !== 0) return;
				if (isInteractiveTarget(event.target)) return;
				var rect = modal.getBoundingClientRect();
				drag = { pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, left: rect.left, top: rect.top };
				modal.classList.add('gws-modal-dragging');
				document.body.classList.add('gws-modal-dragging-active');
				handle.setPointerCapture(event.pointerId);
				event.preventDefault();
			}

			function pointerMove(event) {
				if (!drag || event.pointerId !== drag.pointerId) return;
				var minVisible = 60;
				var left = drag.left + event.clientX - drag.startX;
				var top = drag.top + event.clientY - drag.startY;
				left = Math.max(minVisible - modal.offsetWidth, Math.min(left, window.innerWidth - minVisible));
				top = Math.max(0, Math.min(top, window.innerHeight - minVisible));
				modal.style.left = left + 'px';
				modal.style.top = top + 'px';
				event.preventDefault();
			}

			function pointerUp(event) {
				if (!drag || event.pointerId !== drag.pointerId) return;
				drag = null;
				modal.classList.remove('gws-modal-dragging');
				document.body.classList.remove('gws-modal-dragging-active');
			}

			handle.addEventListener('pointerdown', pointerDown);
			handle.addEventListener('pointermove', pointerMove);
			handle.addEventListener('pointerup', pointerUp);
			handle.addEventListener('pointercancel', pointerUp);
			quickNoteCleanups[elementId] = function () {
				handle.removeEventListener('pointerdown', pointerDown);
				handle.removeEventListener('pointermove', pointerMove);
				handle.removeEventListener('pointerup', pointerUp);
				handle.removeEventListener('pointercancel', pointerUp);
				document.body.classList.remove('gws-modal-dragging-active');
			};
		},
		toggleExpanded: function (elementId) {
			var modal = document.getElementById(elementId);
			if (!modal) return;
			var button = modal.querySelector('[data-qn-expand-button]');
			var icon = button && button.querySelector('i');
			if (modal.dataset.expanded === 'true') {
				modal.style.left = modal.dataset.restoreLeft || '120px';
				modal.style.top = modal.dataset.restoreTop || '100px';
				modal.style.width = modal.dataset.restoreWidth || '380px';
				modal.style.height = modal.dataset.restoreHeight || '';
				modal.dataset.expanded = 'false';
				if (button) { button.title = 'Expand Quick Note'; button.setAttribute('aria-label', 'Expand Quick Note'); }
				if (icon) { icon.classList.remove('bi-fullscreen-exit'); icon.classList.add('bi-arrows-fullscreen'); }
				return;
			}
			var rect = modal.getBoundingClientRect();
			modal.dataset.restoreLeft = rect.left + 'px';
			modal.dataset.restoreTop = rect.top + 'px';
			modal.dataset.restoreWidth = rect.width + 'px';
			modal.dataset.restoreHeight = modal.style.height;
			modal.style.left = '2vw';
			modal.style.top = '2vh';
			modal.style.width = '96vw';
			modal.style.height = '92vh';
			modal.dataset.expanded = 'true';
			if (button) { button.title = 'Restore Quick Note'; button.setAttribute('aria-label', 'Restore Quick Note'); }
			if (icon) { icon.classList.remove('bi-arrows-fullscreen'); icon.classList.add('bi-fullscreen-exit'); }
		},
		destroy: function (elementId) {
			if (quickNoteCleanups[elementId]) quickNoteCleanups[elementId]();
			delete quickNoteCleanups[elementId];
		}
	};

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

	window.sentinelGptChat = {
		scrollToBottom: function (thread) {
			if (!(thread instanceof HTMLElement)) return;
			thread.scrollTop = thread.scrollHeight;
		}
	};
})();
