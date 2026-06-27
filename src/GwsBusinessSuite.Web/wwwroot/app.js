// Sidebar collapse is plain DOM/CSS rather than a Blazor event handler because several
// admin pages (e.g. the Dashboard) render statically with no interactive circuit, where
// @onclick never wires up. Re-run after every Blazor enhanced-navigation swap since that
// replaces the DOM (and the toggle button's bound listener) without a full page load.
function gwsApplySidebarToggleLabel(layout, toggleBtn) {
	var collapsed = layout.classList.contains('gws-sidebar-collapsed');
	toggleBtn.title = collapsed ? 'Expand sidebar' : 'Collapse sidebar';
}

function gwsInitSidebarToggle() {
	var layout = document.querySelector('.gws-layout');
	var toggleBtn = document.querySelector('.gws-sidebar-toggle');
	if (!layout || !toggleBtn) {
		return;
	}

	if (localStorage.getItem('gws-sidebar-collapsed') === 'true') {
		layout.classList.add('gws-sidebar-collapsed');
	}
	gwsApplySidebarToggleLabel(layout, toggleBtn);

	toggleBtn.addEventListener('click', function () {
		var collapsed = layout.classList.toggle('gws-sidebar-collapsed');
		localStorage.setItem('gws-sidebar-collapsed', collapsed ? 'true' : 'false');
		gwsApplySidebarToggleLabel(layout, toggleBtn);
	});
}

document.addEventListener('DOMContentLoaded', gwsInitSidebarToggle);
document.addEventListener('blazor:enhancedload', gwsInitSidebarToggle);

window.gwsTooltips = {
	init: function (selector) {
		if (!window.bootstrap || !window.bootstrap.Tooltip) {
			return;
		}

		var targets = document.querySelectorAll(
			selector || '[data-bs-toggle="tooltip"]',
		);
		targets.forEach(function (el) {
			var existing = window.bootstrap.Tooltip.getInstance(el);
			if (existing) {
				existing.dispose();
			}

			new window.bootstrap.Tooltip(el, {
				html: true,
				boundary: 'viewport',
			});
		});
	},
};
