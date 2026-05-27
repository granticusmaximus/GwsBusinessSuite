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
