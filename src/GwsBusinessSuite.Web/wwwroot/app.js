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
		setMenuButtonsExpanded(false);
	}

	// The phone bar's Menu button (MainLayout) opens the same drawer as the top bar's toggle.
	function setMenuButtonsExpanded(expanded) {
		document.querySelectorAll('[data-gws-nav-toggle]').forEach(function (button) {
			button.setAttribute('aria-expanded', expanded ? 'true' : 'false');
		});
	}

	// The workspace rail (NavMenu): show one workspace's sub-nav section without a round trip.
	// The server renders the current page's workspace, and enhanced navigation re-renders it, so
	// nothing here is stored. Picking a workspace while collapsed expands the sidebar.
	function showWorkspace(key) {
		document.querySelectorAll('.gws-sidebar [data-workspace]').forEach(function (section) {
			section.hidden = section.dataset.workspace !== key;
		});
		document.querySelectorAll('.gws-sidebar [data-gws-workspace]').forEach(function (button) {
			var active = button.dataset.gwsWorkspace === key;
			button.classList.toggle('is-active', active);
			button.setAttribute('aria-pressed', active ? 'true' : 'false');
		});
		var layout = shell();
		if (layout && !isMobile() && layout.classList.contains('gws-sidebar-collapsed')) toggleNavigation();
	}

	// The phone bar's SentinelGPT button drives the panel's own (interactive) top-bar toggle,
	// which the phone layout hides.
	function openAssistant() {
		var toggle = document.querySelector('.gws-assistant-toggle');
		if (toggle && !toggle.classList.contains('is-active')) toggle.click();
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
			setMenuButtonsExpanded(mobileOpen);
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

	// Dark stays the app's default (unset attribute) - only an explicit light choice ever writes
	// the attribute/storage key, so anyone who never touches this toggle sees exactly the look
	// this app always had. See App.razor's inline head script for the matching pre-paint read.
	function toggleTheme() {
		var current = document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
		var next = current === 'light' ? 'dark' : 'light';
		document.documentElement.setAttribute('data-theme', next);
		try { localStorage.setItem('gwsTheme', next); } catch (e) { /* storage unavailable */ }
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
					var first = document.querySelector('.gws-sidebar [data-workspace]:not([hidden]) .gws-nav-link')
						|| document.querySelector('.gws-sidebar .gws-nav-link');
					if (first) first.focus();
				}, 80);
			}
			return;
		}

		var collapsed = layout.classList.toggle('gws-sidebar-collapsed');
		localStorage.setItem('gws-sidebar-collapsed', collapsed ? 'true' : 'false');
		applySidebarState();
	}

	// Client-side-only "focus mode" toggle - deliberately not routed through Blazor Server (no
	// @onclick, no circuit round-trip) since it's a pure layout change with zero server-side
	// state. Reuses .gws-builder-mode/.gws-content-builder, the exact same classes
	// MainLayout.razor's IsChromelessRoute already applies for the canvas editor's own permanent
	// chromeless route - the only addition needed was a CSS rule to actually hide the admin
	// bar/sidebar (see app.css), since on a route where IsChromelessRoute is false those elements
	// are real DOM nodes, unlike the canvas route where they're never rendered at all. Not
	// persisted to localStorage on purpose (unlike gws-sidebar-collapsed) - this is a transient,
	// per-visit focus state, not a standing layout preference, so it always resets to normal chrome
	// on a fresh page load.
	function isFocusModeOn() {
		var layout = shell();
		return !!layout && layout.classList.contains('gws-focus-mode');
	}

	function setFocusMode(on) {
		var layout = shell();
		var content = document.querySelector('.gws-content');
		if (!layout) return;
		layout.classList.toggle('gws-builder-mode', on);
		layout.classList.toggle('gws-focus-mode', on);
		if (content) content.classList.toggle('gws-content-builder', on);
		document.querySelectorAll('[data-gws-focus-toggle]').forEach(function (button) {
			button.classList.toggle('is-active', on);
			button.title = on ? 'Exit fullscreen' : 'Fullscreen';
			button.setAttribute('aria-label', button.title);
			button.setAttribute('aria-pressed', on ? 'true' : 'false');
			var icon = button.querySelector('i');
			if (icon) icon.className = on ? 'bi bi-fullscreen-exit' : 'bi bi-arrows-fullscreen';
		});
	}

	function toggleFocusMode() {
		setFocusMode(!isFocusModeOn());
	}

	function commandElements() {
		return {
			layer: document.querySelector('[data-gws-command-layer]'),
			input: document.querySelector('[data-gws-command-input]'),
			results: document.querySelector('[data-gws-command-results]')
		};
	}

	function commandEntries() {
		// The sidebar's links, plus hidden entries for each tab of a merged app (NavMenu's
		// .gws-command-extra), which name their app in data-command-group.
		return Array.from(document.querySelectorAll('.gws-sidebar .gws-nav-link, .gws-sidebar .gws-command-extra')).map(function (link) {
			var copy = link.querySelector('.gws-nav-link-copy');
			var title = copy && copy.firstElementChild ? copy.firstElementChild.textContent.trim() : link.textContent.trim();
			var description = copy && copy.querySelector('small') ? copy.querySelector('small').textContent.trim() : '';
			var workspace = link.closest('[data-workspace-label]');
			var groupName = link.dataset.commandGroup || (workspace ? workspace.dataset.workspaceLabel : 'General');
			var icon = link.querySelector('i');
			return { title: title, description: description, group: groupName, href: link.href, iconClass: icon ? icon.className : 'bi bi-arrow-right' };
		});
	}

	// Palette 2.0 (tech-forward T1): sections in order - Pinned and Recent (empty search only),
	// Actions, Pages, then records grouped by kind from /admin/api/suite-search, with the match
	// highlighted. "t:ticket acme" searches one kind of record (SuiteSearchQuery on the server).
	var TYPE_FILTER = /(^|\s)(t|type):\S+/i;
	var commandShortcuts = null;

	function sidebarHas(path) {
		return Array.from(document.querySelectorAll('.gws-sidebar a[href]')).some(function (link) {
			return new URL(link.href).pathname === '/' + path;
		});
	}

	// A create action shows only when its page is in this person's sidebar (the same role rules);
	// ?new=1 makes the page start its own "new" action.
	function commandActions() {
		return [
			{ title: 'New contact', description: 'Add a contact in CRM', href: '/admin/crm?new=1', iconClass: 'bi bi-person-plus', page: 'admin/crm' },
			{ title: 'New invoice', description: 'Draft an invoice in Billing', href: '/admin/billing?new=1', iconClass: 'bi bi-receipt', page: 'admin/billing' },
			{ title: 'New ticket', description: 'Open a support ticket', href: '/admin/support?new=1', iconClass: 'bi bi-life-preserver', page: 'admin/support' },
			{ title: 'New page', description: 'Add a page to the site', href: '/admin/pages/all?new=1', iconClass: 'bi bi-file-earmark-plus', page: 'admin/pages' },
			{ title: 'New post', description: 'Start a blog post', href: '/admin/article-editor?new=1', iconClass: 'bi bi-pencil-square', page: 'admin/article-editor' },
			{ title: 'New workflow', description: 'Build an automation', href: '/admin/automation?new=1', iconClass: 'bi bi-diagram-3', page: 'admin/automation' },
			{ title: 'Switch theme', description: 'Light or dark', run: 'theme', iconClass: 'bi bi-circle-half' },
			{ title: 'Keyboard shortcuts', description: 'Every key the admin knows', run: 'shortcuts', iconClass: 'bi bi-keyboard' },
			{ title: 'Sign out', description: 'End this session', href: '/auth/logout', iconClass: 'bi bi-box-arrow-right' }
		].filter(function (action) { return !action.page || sidebarHas(action.page); })
			.map(function (action) { action.group = 'Action'; return action; });
	}

	function appendHighlighted(element, text, query) {
		var at = query ? text.toLowerCase().indexOf(query) : -1;
		if (at < 0) { element.textContent = text; return; }
		var mark = document.createElement('mark');
		mark.textContent = text.slice(at, at + query.length);
		element.append(text.slice(0, at), mark, text.slice(at + query.length));
	}

	function appendGroup(container, label) {
		var heading = document.createElement('div');
		heading.className = 'gws-command-group';
		heading.textContent = label;
		container.appendChild(heading);
	}

	function appendResultEntries(container, entries, query) {
		entries.forEach(function (entry) {
			var item = document.createElement(entry.run ? 'button' : 'a');
			var index = document.querySelectorAll('[data-command-result]').length;
			item.className = 'gws-command-result' + (index === commandIndex ? ' is-selected' : '');
			if (entry.run) { item.type = 'button'; item.dataset.commandRun = entry.run; }
			else {
				item.href = entry.href;
				var previewKey = previewKeyForHref(entry.href);
				if (previewKey) item.dataset.palettePreview = previewKey;
			}
			item.dataset.commandResult = '';
			item.innerHTML = '<i aria-hidden="true"></i><span><strong></strong><small></small></span><em></em>';
			item.querySelector('i').className = entry.iconClass;
			appendHighlighted(item.querySelector('strong'), entry.title, query);
			item.querySelector('small').textContent = entry.description || '';
			item.querySelector('em').textContent = entry.group;
			container.appendChild(item);
		});
	}

	function clearCommandEmptyState() {
		var elements = commandElements();
		if (!elements.results) return;
		var empty = elements.results.querySelector('.gws-command-empty');
		if (empty) empty.remove();
	}

	// While the record search is still out (the first semantic search of a session can take a
	// few seconds), say so rather than claiming nothing matches.
	function showCommandEmptyStateIfNoResults(searching) {
		var elements = commandElements();
		if (!elements.results || elements.results.querySelector('[data-command-result]')) return;
		if (elements.results.querySelector('.gws-command-empty')) return;
		var empty = document.createElement('div');
		empty.className = 'gws-command-empty';
		empty.textContent = searching ? 'Searching records…' : 'No pages, tools, or records match your search.';
		elements.results.appendChild(empty);
	}

	function rank(entry, normalized) {
		var title = entry.title.toLowerCase();
		return !normalized ? 0
			: title.startsWith(normalized) ? 0
			: title.includes(normalized) ? 1
			: [entry.description, entry.group].join(' ').toLowerCase().includes(normalized) ? 2
			: -1;
	}

	function ranked(entries, normalized, limit) {
		return entries.map(function (entry, position) { return { entry: entry, rank: rank(entry, normalized), position: position }; })
			.filter(function (item) { return item.rank >= 0; })
			.sort(function (a, b) { return a.rank - b.rank || a.position - b.position; })
			.slice(0, limit)
			.map(function (item) { return item.entry; });
	}

	function shortcutEntries(list, group) {
		return (list || []).map(function (shortcut) {
			return { title: shortcut.title, description: '/' + shortcut.url, group: group, href: '/' + shortcut.url, iconClass: 'bi ' + (shortcut.icon || 'bi-clock-history') };
		});
	}

	// Nav entries, actions and shortcuts render instantly from what the page already has. Live
	// records come from a separate, debounced fetch appended below them once it resolves, so the
	// palette never waits on a round trip just to jump to a known page.
	var suiteSearchAbortController = null;
	var suiteSearchDebounceTimer = null;
	// SentinelGPT in ⌘K (tech-forward T1b): a plain-language request is also sent to
	// /admin/api/suite-search/ask, which turns it into one structured search; its results join
	// at the top as their own group, without moving the selection.
	var askAbortController = null;
	var askDebounceTimer = null;
	var lastCommandQuery = '';

	function looksLikeRequest(raw) {
		return !TYPE_FILTER.test(raw) && raw.length >= 3 && raw.length <= 200
			&& (/\?$/.test(raw) || raw.split(/\s+/).length >= 3);
	}

	function askAction(raw) {
		return { title: 'Ask SentinelGPT', description: '“' + raw + '”', run: 'ask', iconClass: 'bi bi-stars', group: 'Action' };
	}

	function renderCommandResults(query) {
		var elements = commandElements();
		if (!elements.results) return;
		var raw = (query || '').trim();
		var filtered = TYPE_FILTER.test(raw);
		var normalized = raw.toLowerCase();
		lastCommandQuery = raw;
		elements.results.replaceChildren();
		commandIndex = -1;
		var sections = [];
		if (!normalized && commandShortcuts) {
			sections.push(['Pinned', shortcutEntries(commandShortcuts.pins, 'Pinned').slice(0, 4)]);
			sections.push(['Recent', shortcutEntries(commandShortcuts.recents, 'Recent').slice(0, 5)]);
		}
		if (!filtered) {
			var ask = raw.split(/\s+/).length >= 2 ? [askAction(raw)] : [];
			sections.push(['Actions', ask.concat(ranked(commandActions(), normalized, normalized ? 4 : 3))]);
			sections.push(['Pages', ranked(commandEntries(), normalized, normalized ? 8 : 6)]);
		}
		sections.forEach(function (section) {
			if (!section[1].length) return;
			if (commandIndex === -1) commandIndex = document.querySelectorAll('[data-command-result]').length;
			appendGroup(elements.results, section[0]);
			appendResultEntries(elements.results, section[1], normalized);
		});
		showCommandEmptyStateIfNoResults(filtered || raw.length >= 2);
		scheduleSuiteSearch(raw, filtered);
		scheduleAsk(raw);
		updatePalettePreview();
	}

	function scheduleAsk(query) {
		window.clearTimeout(askDebounceTimer);
		if (askAbortController) askAbortController.abort();
		if (!looksLikeRequest(query)) return;
		askDebounceTimer = window.setTimeout(function () { fetchAsk(query); }, 700);
	}

	function fetchAsk(query) {
		var elements = commandElements();
		if (!elements.results) return;
		var group = document.createElement('div');
		group.className = 'gws-command-ai';
		appendGroup(group, 'SentinelGPT');
		var status = document.createElement('div');
		status.className = 'gws-command-ai-status';
		status.textContent = 'Turning your request into a search…';
		group.appendChild(status);
		elements.results.prepend(group);
		askAbortController = new AbortController();
		fetch('/admin/api/suite-search/ask?q=' + encodeURIComponent(query), { signal: askAbortController.signal, headers: { Accept: 'application/json' } })
			.then(function (response) { return response.status === 200 ? response.json() : null; })
			.then(function (answer) {
				if (!elements.input || elements.input.value.trim() !== query || !group.isConnected) return;
				if (!answer || !answer.results || !answer.results.length) {
					status.textContent = answer
						? 'SentinelGPT looked for “' + answer.summary + '” and found nothing.'
						: 'SentinelGPT couldn’t turn that into a search.';
					return;
				}
				var selected = elements.results.querySelector('[data-command-result].is-selected');
				group.replaceChildren();
				appendGroup(group, 'SentinelGPT · ' + answer.summary);
				appendResultEntries(group, answer.results.map(function (record) {
					return { title: record.title, description: record.subtitle, group: record.category, href: record.url, iconClass: record.iconClass };
				}), '');
				// Keep the selection where it was (the new rows sit above it), or start on the
				// first answer when nothing was selected.
				group.querySelectorAll('.is-selected').forEach(function (item) { if (item !== selected) item.classList.remove('is-selected'); });
				var all = Array.from(elements.results.querySelectorAll('[data-command-result]'));
				if (selected) commandIndex = all.indexOf(selected);
				else { commandIndex = 0; all[0].classList.add('is-selected'); }
				clearCommandEmptyState();
				updatePalettePreview();
			})
			.catch(function (error) {
				if (error && error.name === 'AbortError') return;
				status.textContent = 'SentinelGPT couldn’t turn that into a search.';
			});
	}

	function scheduleSuiteSearch(query, filtered) {
		window.clearTimeout(suiteSearchDebounceTimer);
		if (suiteSearchAbortController) suiteSearchAbortController.abort();
		if (!filtered && query.length < 2) return;
		suiteSearchDebounceTimer = window.setTimeout(function () { fetchSuiteSearchResults(query); }, 200);
	}

	var PLURALS = { 'CRM contact': 'Contacts', 'CRM deal': 'Deals', 'CMS page': 'Site pages', 'Article': 'Posts', 'Automation': 'Workflows',
		'Support ticket': 'Tickets', 'Billing invoice': 'Invoices', 'Sentinel page': 'Sentinel pages', 'Sentinel database': 'Sentinel databases', 'Affiliate offer': 'Affiliate offers' };

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
				var text = query.replace(TYPE_FILTER, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
				var byKind = {};
				(records || []).forEach(function (record) {
					(byKind[record.category] = byKind[record.category] || []).push(record);
				});
				Object.keys(byKind).forEach(function (kind) {
					appendGroup(elements.results, PLURALS[kind] || kind);
					appendResultEntries(elements.results, byKind[kind].map(function (record) {
						return { title: record.title, description: record.subtitle, group: record.category, href: record.url, iconClass: record.iconClass };
					}), text);
				});
				if (commandIndex === -1 && elements.results.querySelector('[data-command-result]')) {
					commandIndex = 0;
					elements.results.querySelector('[data-command-result]').classList.add('is-selected');
				}
				showCommandEmptyStateIfNoResults();
				updatePalettePreview();
			})
			.catch(function (error) {
				// Aborted for a newer query: that one renders. Offline: stop saying "Searching".
				if (error && error.name === 'AbortError') return;
				if (!elements.input || elements.input.value.trim() !== query) return;
				clearCommandEmptyState();
				showCommandEmptyStateIfNoResults();
			});
	}

	// The person's pins and recent records (the rail's, R1b) for the empty palette; fetched each
	// time it opens so a pin made a moment ago shows.
	function loadCommandShortcuts() {
		fetch('/admin/api/shortcuts', { headers: { Accept: 'application/json' } })
			.then(function (response) { return response.ok ? response.json() : null; })
			.then(function (shortcuts) {
				commandShortcuts = shortcuts;
				var elements = commandElements();
				if (shortcuts && elements.input && !elements.layer.hidden && !elements.input.value.trim()) renderCommandResults('');
			})
			.catch(function () { /* offline - the palette still lists actions and pages */ });
	}

	function runCommand(name) {
		closeCommand();
		if (name === 'theme') toggleTheme();
		else if (name === 'shortcuts') openShortcuts();
		else if (name === 'ask') window.gwsAssistant.ask(lastCommandQuery);
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
		loadCommandShortcuts();
		// Focus now, and again after the current render, in case a page finishing its own render
		// moves focus at the same moment.
		elements.input.focus();
		window.setTimeout(function () { elements.input.focus(); }, 0);
	}

	function closeCommand() {
		var elements = commandElements();
		if (!elements.layer || elements.layer.hidden) return;
		elements.layer.hidden = true;
		document.body.classList.remove('gws-no-scroll');
		var trigger = Array.from(document.querySelectorAll('[data-gws-command-open]')).find(function (button) {
			return button.offsetParent !== null;
		});
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
		updatePalettePreview();
	}

	// Previews (tech-forward T2): a contact, ticket or invoice summarised by
	// /admin/api/preview/{kind}/{id}, shown as a card over anything marked data-preview="kind:id"
	// (after 300 ms of hover, or on keyboard focus) and in the palette's side pane for the
	// highlighted result. One request per record per page; touch screens skip the card (a tap
	// opens the record).
	var previewCache = new Map();
	var previewShowTimer = null;
	var previewHideTimer = null;
	var previewTarget = null;

	function loadPreview(key) {
		if (!previewCache.has(key)) {
			var parts = key.split(':');
			previewCache.set(key, fetch('/admin/api/preview/' + encodeURIComponent(parts[0]) + '/' + encodeURIComponent(parts[1]), { headers: { Accept: 'application/json' } })
				.then(function (response) { return response.ok ? response.json() : null; })
				.catch(function () { return null; }));
		}
		return previewCache.get(key);
	}

	function renderPreview(container, preview) {
		container.replaceChildren();
		if (preview.status) {
			var pill = document.createElement('span');
			pill.className = 'gws-pill gws-pill-' + preview.tone;
			pill.textContent = preview.status;
			container.appendChild(pill);
		}
		var title = document.createElement('strong');
		title.className = 'gws-preview-title';
		title.textContent = preview.title;
		container.appendChild(title);
		var facts = document.createElement('dl');
		facts.className = 'gws-preview-facts';
		(preview.facts || []).forEach(function (fact) {
			var term = document.createElement('dt');
			term.textContent = fact.label;
			var value = document.createElement('dd');
			value.textContent = fact.value;
			facts.append(term, value);
		});
		container.appendChild(facts);
		var actions = document.createElement('div');
		actions.className = 'gws-preview-actions';
		(preview.actions || []).forEach(function (action) {
			var link = document.createElement('a');
			link.className = 'btn btn-sm btn-outline-secondary';
			link.href = action.url;
			link.textContent = action.label;
			actions.appendChild(link);
		});
		container.appendChild(actions);
	}

	function previewCard() {
		return document.querySelector('[data-gws-preview-card]');
	}

	function placePreviewCard(card, target) {
		var rect = target.getBoundingClientRect();
		card.style.left = '0px';
		card.style.top = '0px';
		var width = card.offsetWidth;
		var height = card.offsetHeight;
		var left = Math.min(Math.max(8, rect.left), window.innerWidth - width - 8);
		var below = rect.bottom + 8;
		var top = below + height > window.innerHeight - 8 ? Math.max(8, rect.top - height - 8) : below;
		card.style.left = left + 'px';
		card.style.top = top + 'px';
	}

	function showPreviewFor(target) {
		var key = target.getAttribute('data-preview');
		var card = previewCard();
		if (!key || !card) return;
		previewTarget = target;
		loadPreview(key).then(function (preview) {
			if (!preview || previewTarget !== target || !document.contains(target)) return;
			renderPreview(card, preview);
			card.hidden = false;
			placePreviewCard(card, target);
		});
	}

	function schedulePreview(target) {
		if (window.matchMedia('(pointer: coarse)').matches) return;
		var command = commandElements();
		if (command.layer && !command.layer.hidden) return;
		window.clearTimeout(previewHideTimer);
		if (previewTarget === target) return;
		window.clearTimeout(previewShowTimer);
		previewShowTimer = window.setTimeout(function () { showPreviewFor(target); }, 300);
	}

	function hidePreview(delay) {
		window.clearTimeout(previewShowTimer);
		window.clearTimeout(previewHideTimer);
		previewHideTimer = window.setTimeout(function () {
			var card = previewCard();
			if (card) card.hidden = true;
			previewTarget = null;
		}, delay || 0);
	}

	// The palette's pane follows its highlighted result: a ⌘K result for a contact, ticket or
	// invoice carries data-palette-preview (from its address).
	function previewKeyForHref(href) {
		var url;
		try { url = new URL(href, window.location.origin); } catch (e) { return null; }
		var contact = url.pathname.match(/^\/admin\/crm\/contacts\/([0-9a-f-]{36})$/i);
		if (contact) return 'contact:' + contact[1];
		if (url.pathname === '/admin/support' && url.searchParams.get('ticket')) return 'ticket:' + url.searchParams.get('ticket');
		if (url.pathname === '/admin/billing' && url.searchParams.get('invoice')) return 'invoice:' + url.searchParams.get('invoice');
		return null;
	}

	var palettePreviewTimer = null;

	function updatePalettePreview() {
		window.clearTimeout(palettePreviewTimer);
		palettePreviewTimer = window.setTimeout(function () {
			var pane = document.querySelector('[data-command-preview]');
			var dialog = pane && pane.closest('.gws-command-dialog');
			if (!pane || !dialog) return;
			var selected = document.querySelector('[data-command-result].is-selected');
			var key = selected && selected.dataset.palettePreview;
			if (!key || !window.matchMedia('(min-width: 1024px)').matches) {
				pane.hidden = true;
				dialog.classList.remove('has-preview');
				return;
			}
			loadPreview(key).then(function (preview) {
				var current = document.querySelector('[data-command-result].is-selected');
				if (!current || current.dataset.palettePreview !== key) return;
				if (!preview) { pane.hidden = true; dialog.classList.remove('has-preview'); return; }
				renderPreview(pane, preview);
				pane.hidden = false;
				dialog.classList.add('has-preview');
			});
		}, 120);
	}

	// Keyboard shortcuts (tech-forward T1): g then a letter jumps to a workspace, j/k move a
	// cursor through the page's list and Enter opens the row, / focuses the page's search, ?
	// lists them all. Off while typing, with a modifier held, while a dialog is open, and on
	// pages with keys of their own (Sentinel, Overwatch, the page editor).
	var WORKSPACE_KEYS = { h: 'home', c: 'content', r: 'relationships', k: 'knowledge', i: 'intelligence', g: 'growth', p: 'platform' };
	var pendingG = 0;
	var rowCursor = null;

	function shortcutsLayer() {
		return document.querySelector('[data-gws-shortcuts-layer]');
	}

	function openShortcuts() {
		var layer = shortcutsLayer();
		if (!layer) return;
		layer.hidden = false;
		var close = layer.querySelector('.gws-shortcuts-close');
		if (close) close.focus();
	}

	function closeShortcuts() {
		var layer = shortcutsLayer();
		if (!layer || layer.hidden) return false;
		layer.hidden = true;
		return true;
	}

	function shortcutsBlocked(event) {
		if (event.metaKey || event.ctrlKey || event.altKey) return true;
		var target = event.target;
		if (target instanceof Element && target.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"]')) return true;
		if (document.querySelector('.sentinel-workspace, .tg-shell, .gws-builder-mode, .modal.d-block, [data-gws-no-shortcuts]')) return true;
		var command = commandElements();
		var shortcuts = shortcutsLayer();
		return !!((command.layer && !command.layer.hidden) || (shortcuts && !shortcuts.hidden));
	}

	function listRows() {
		return Array.from(document.querySelectorAll('.gws-content table.table > tbody > tr, .gws-content .list-group > .list-group-item'))
			.filter(function (row) { return row.offsetParent !== null; });
	}

	function clearRowCursor() {
		if (rowCursor) rowCursor.classList.remove('gws-row-cursor');
		rowCursor = null;
	}

	function moveRowCursor(direction) {
		var rows = listRows();
		if (!rows.length) return;
		var index = rowCursor ? rows.indexOf(rowCursor) : -1;
		index = index === -1 ? (direction > 0 ? 0 : rows.length - 1) : Math.min(rows.length - 1, Math.max(0, index + direction));
		clearRowCursor();
		rowCursor = rows[index];
		rowCursor.classList.add('gws-row-cursor');
		rowCursor.scrollIntoView({ block: 'nearest' });
	}

	function goToWorkspace(name) {
		var link = document.querySelector('.gws-sidebar [data-workspace="' + name + '"] a.gws-nav-link');
		if (link) link.click();
	}

	function handleShortcut(event) {
		if (shortcutsBlocked(event)) return;
		var key = event.key;
		if (pendingG && Date.now() - pendingG < 1500) {
			pendingG = 0;
			var workspace = WORKSPACE_KEYS[key.toLowerCase()];
			if (workspace) { event.preventDefault(); goToWorkspace(workspace); }
			return;
		}
		pendingG = 0;
		if (key === 'g') { pendingG = Date.now(); return; }
		if (key === '?') { event.preventDefault(); openShortcuts(); return; }
		if (key === '/') {
			var search = document.querySelector('.gws-content input[type="search"]');
			if (search) { event.preventDefault(); search.focus(); }
			return;
		}
		if (key === 'j' || key === 'k') { event.preventDefault(); moveRowCursor(key === 'j' ? 1 : -1); return; }
		// x checks the row under the cursor (shift+x extends the range), for the bulk bar.
		if ((key === 'x' || key === 'X') && rowCursor && document.contains(rowCursor)) {
			var check = rowCursor.querySelector('input[data-row-select]');
			if (check) {
				event.preventDefault();
				check.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, shiftKey: event.shiftKey }));
			}
			return;
		}
		if (key === 'Enter' && rowCursor && document.contains(rowCursor)) {
			var open = rowCursor.querySelector('a[href], button.btn-link, button');
			if (open) { event.preventDefault(); open.click(); }
		}
	}


	// An empty list's first action (EmptyState) points at the page's own form: bring its first
	// field into view and focus it, rather than duplicating the form.
	function focusField(selector) {
		var field = selector ? document.querySelector(selector) : null;
		if (!field) return;
		field.scrollIntoView({ block: 'center', behavior: 'smooth' });
		field.focus({ preventScroll: true });
		// A file input's "field" is its picker (Media's Upload image). This runs inside the
		// person's click, so the browser allows opening it.
		if (field.matches('input[type="file"]') && !field.disabled) {
			field.click();
			return;
		}
		// The same click often resets the form on the server (Billing's New invoice), and an
		// EditForm given a new model rebuilds its fields, dropping this focus when the render
		// arrives. Follow the field to its replacement, unless focus has moved on by then.
		var observer = new MutationObserver(function () {
			if (field.isConnected) return;
			observer.disconnect();
			var replacement = document.querySelector(selector);
			var active = document.activeElement;
			if (replacement && (!active || active === document.body)) replacement.focus({ preventScroll: true });
		});
		observer.observe(document.body, { childList: true, subtree: true });
		setTimeout(function () { observer.disconnect(); }, 3000);
	}

	function bindShellEventsOnce() {
		if (shellEventsBound) return;
		shellEventsBound = true;

		document.addEventListener('click', function (event) {
			var workspaceButton = event.target.closest('[data-gws-workspace]');
			if (workspaceButton) showWorkspace(workspaceButton.dataset.gwsWorkspace);
			if (event.target.closest('.gws-sidebar-toggle, [data-gws-nav-toggle]')) toggleNavigation();
			if (event.target.closest('[data-gws-sidebar-close]')) closeMobileNavigation();
			if (event.target.closest('.gws-sidebar .gws-nav-link, .gws-sidebar a.gws-rail-item, .gws-bottom-bar a') && isMobile()) closeMobileNavigation();
			if (event.target.closest('[data-gws-assistant-open]')) { if (isMobile()) closeMobileNavigation(); openAssistant(); }
			if (event.target.closest('[data-gws-command-open]')) { if (isMobile()) closeMobileNavigation(); openCommand(); }
			if (event.target.closest('[data-gws-command-close]')) closeCommand();
			// Opening a result closes the palette (it's data-permanent, so the page change won't).
			if (event.target.closest('a[data-command-result]')) closeCommand();
			var runButton = event.target.closest('[data-command-run]');
			if (runButton) runCommand(runButton.dataset.commandRun);
			if (event.target.closest('[data-gws-shortcuts-close]')) closeShortcuts();
			if (event.target.closest('[data-gws-theme-toggle]')) toggleTheme();
			if (event.target.closest('[data-gws-focus-toggle]')) toggleFocusMode();
			if (event.target.closest('[data-gws-print]')) window.print();
			var focusTrigger = event.target.closest('[data-gws-focus]');
			if (focusTrigger) focusField(focusTrigger.getAttribute('data-gws-focus'));
		});

		document.addEventListener('input', function (event) {
			if (event.target.matches('[data-gws-command-input]')) renderCommandResults(event.target.value);
		});

		document.addEventListener('mouseover', function (event) {
			if (!(event.target instanceof Element)) return;
			if (event.target.closest('[data-gws-preview-card]')) { window.clearTimeout(previewHideTimer); return; }
			var target = event.target.closest('[data-preview]');
			if (target) schedulePreview(target);
		});
		document.addEventListener('mouseout', function (event) {
			if (!(event.target instanceof Element)) return;
			var from = event.target.closest('[data-preview], [data-gws-preview-card]');
			if (!from || (event.relatedTarget instanceof Element && from.contains(event.relatedTarget))) return;
			hidePreview(200);
		});
		document.addEventListener('focusin', function (event) {
			var target = event.target instanceof Element ? event.target.closest('[data-preview]') : null;
			if (target) schedulePreview(target); else if (previewTarget) hidePreview();
		});

		// The palette and the shortcut sheet are modal: focus stays inside them. Without this, a
		// page finishing its navigation (FocusOnNavigate moves focus to its h1) took focus out of
		// the palette mid-word, and the rest of the typing landed on the page.
		document.addEventListener('focusin', function (event) {
			var command = commandElements();
			if (command.layer && !command.layer.hidden && !command.layer.contains(event.target)) { command.input.focus(); return; }
			var sheet = shortcutsLayer();
			if (sheet && !sheet.hidden && !sheet.contains(event.target)) {
				var close = sheet.querySelector('.gws-shortcuts-close');
				if (close) close.focus();
			}
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
				if (previewTarget) hidePreview();
				if (commandOpen) closeCommand();
				else if (!closeShortcuts()) { clearRowCursor(); closeMobileNavigation(); }
			} else if (commandOpen && event.key === 'ArrowDown') {
				event.preventDefault();
				moveCommandSelection(1);
			} else if (commandOpen && event.key === 'ArrowUp') {
				event.preventDefault();
				moveCommandSelection(-1);
			} else if (commandOpen && event.key === 'Enter') {
				var selected = document.querySelector('[data-command-result].is-selected');
				if (selected) { event.preventDefault(); selected.click(); }
			} else {
				handleShortcut(event);
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
		rowCursor = null;
		previewCache.clear();
		hidePreview();
	}

	// Quick Note uses browser-local pointer movement instead of sending each mousemove over
	// the Blazor Server circuit. Pointer capture keeps the drag continuous outside the header,
	// while the body class prevents accidental text selection on the dashboard underneath.
	// Sentinel's focus mode (Wiki.razor) hides the admin chrome through the same toggle.
	window.gwsFocusMode = { set: setFocusMode, isOn: isFocusModeOn };

	// Pages opened by ⌘K's create actions (?new=1) focus their form's first field through this.
	window.gwsFocus = { field: focusField };

	// A file made in the page (a list's Export CSV, tech-forward T3) handed to the browser.
	window.gwsDownload = {
		text: function (filename, content, type) {
			var url = URL.createObjectURL(new Blob([content], { type: (type || 'text/plain') + ';charset=utf-8' }));
			var link = document.createElement('a');
			link.href = url;
			link.download = filename;
			document.body.appendChild(link);
			link.click();
			link.remove();
			window.setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
		}
	};

	// The account menu's density switch (DensityToggle) saves the choice, then swaps the class the
	// layout rendered, so the change shows without a reload.
	window.gwsDensity = {
		set: function (compact) {
			var layout = shell();
			if (layout) layout.classList.toggle('gws-density-compact', !!compact);
		}
	};

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

	// Brings a record opened from a link (?ticket=, ?invoice=) into view, unless it sits in the
	// top half of the first screen, which Blazor's own scroll-to-top after navigating already
	// shows. Measured against the page, not the window, because that scroll-to-top may still be
	// animating. The admin layout's scroll-padding-top keeps it clear of the sticky bar.
	// The SentinelGPT side panel reads the page it sits on: the title, the main heading, the
	// content area's text and the last text selected outside the panel on this same page.
	var assistantSelection = { path: '', text: '' };
	document.addEventListener('selectionchange', function () {
		var selection = window.getSelection();
		if (!selection || selection.isCollapsed) return;
		var node = selection.anchorNode;
		var element = node && (node.nodeType === 1 ? node : node.parentElement);
		if (!element || element.closest('.gws-assistant')) return;
		var text = selection.toString().trim();
		if (text) assistantSelection = { path: location.pathname, text: text.slice(0, 8000) };
	});
	window.gwsAssistant = {
		// The panel island registers itself so ⌘K's "Ask SentinelGPT" can open it and ask.
		panel: null,
		register: function (dotnet) { this.panel = dotnet; },
		ask: function (question) {
			if (this.panel) return this.panel.invokeMethodAsync('AskFromPaletteAsync', question).catch(function () { openAssistant(); });
			openAssistant();
			return Promise.resolve();
		},
		widthObservers: new Map(),
		nextWidthObserver: 1,
		selection: function () {
			return assistantSelection.path === location.pathname ? assistantSelection.text : '';
		},
		clearSelection: function () { assistantSelection = { path: '', text: '' }; },
		heading: function () {
			var heading = document.querySelector('main.gws-content h1');
			return heading ? heading.textContent.trim() : '';
		},
		capture: function () {
			var main = document.querySelector('main.gws-content');
			var heading = main && main.querySelector('h1');
			return {
				title: document.title || '',
				heading: heading ? heading.textContent.trim() : '',
				selection: window.gwsAssistant.selection(),
				visibleText: main ? main.innerText.slice(0, 12000) : ''
			};
		},
		copy: function (text) {
			return navigator.clipboard ? navigator.clipboard.writeText(text) : Promise.reject(new Error('Clipboard unavailable'));
		},
		scrollToEnd: function (element) {
			if (element instanceof HTMLElement) element.scrollTop = element.scrollHeight;
		},
		// Room for SentinelGPT and a record inspector side by side (RightPanelState).
		wide: function () { return window.matchMedia('(min-width: 1920px)').matches; },
		observeWidth: function (dotnet) {
			var query = window.matchMedia('(min-width: 1920px)');
			var id = this.nextWidthObserver++;
			var listener = function (event) {
				if (!event.matches) dotnet.invokeMethodAsync('OnRightPanelsNarrowedAsync');
			};
			query.addEventListener('change', listener);
			this.widthObservers.set(id, { query: query, listener: listener });
			return id;
		},
		unobserveWidth: function (id) {
			var observer = this.widthObservers.get(id);
			if (!observer) return;
			observer.query.removeEventListener('change', observer.listener);
			this.widthObservers.delete(id);
		}
	};

	window.gwsScroll = {
		toId: function (id) {
			var element = document.getElementById(id);
			if (!element) return;
			var top = element.getBoundingClientRect().top + window.scrollY;
			if (top < window.innerHeight / 2) return;
			element.scrollIntoView({ behavior: 'smooth', block: 'start' });
		}
	};
})();

// The server renders on UTC; Blazor pages that show a "saved at" time ask the browser for its
// own offset once (minutes WEST of UTC, i.e. Date.getTimezoneOffset()).
window.gwsClientTime = {
	utcOffsetMinutesWest: function () { return new Date().getTimezoneOffset(); }
};
