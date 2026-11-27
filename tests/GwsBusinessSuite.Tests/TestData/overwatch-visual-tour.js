// The Overwatch visual tour (26-11-26, Grant: every layer visually verified on the Mac app and the
// web/PWA). Runs inside /admin/osint on either app. The host supplies three functions:
//   host.shot(name)   -> Promise: save a picture of the window as <name>.png
//   host.select(key)  -> Promise<boolean>: open one pin's window by its real click path
//   host.log(text)    -> void
// For each layer switch it flies somewhere that layer has data, turns it on, waits, takes a picture,
// opens one pin's window and takes another, then turns it off. The switches it found on at the start
// are put back at the end. Returns one line per step for the report.
(function () {
    const STEPS = [
        // key, [lat, lon, height m], wait ms, open a pin's window
        ['cameras', [33.75, -84.39, 60000], 6000, true],
        ['radar', [35.0, -95.0, 2500000], 9000, false],
        ['alerts', [39.0, -98.0, 4500000], 8000, false],
        ['roadtemps', [44.0, -100.0, 2500000], 9000, true],
        ['incidents', [33.75, -84.39, 150000], 8000, true],
        ['signs', [34.05, -118.25, 150000], 9000, true],
        ['city-traffic', [33.75, -84.39, 40000], 9000, false],
        ['live-flow', [33.75, -84.39, 40000], 8000, false],
        ['world-news', [25.0, 10.0, 16000000], 9000, true],
        ['convergence', [25.0, 10.0, 16000000], 8000, true],
        ['conflict', [31.0, 38.0, 4500000], 9000, true],
        ['travel', [10.0, 20.0, 16000000], 8000, true],
        ['humanitarian', [5.0, 20.0, 16000000], 8000, true],
        ['hazards', [20.0, 0.0, 18000000], 9000, true],
        ['wmo', [50.0, 10.0, 5000000], 9000, true],
        ['outages', [37.0, -95.0, 4500000], 9000, true],
        ['internet', [10.0, 20.0, 16000000], 8000, true],
        ['fires', [37.0, -110.0, 3500000], 9000, true],
        ['night-lights', [37.0, -95.0, 5000000], 8000, false],
        ['aircraft', [33.64, -84.43, 250000], 10000, true],
        ['airquality', [37.0, -95.0, 4500000], 9000, true],
        ['airports', [38.0, -95.0, 4500000], 8000, true],
        ['aurora', [65.0, -100.0, 9000000], 8000, false],
        ['satellites', [20.0, 0.0, 20000000], 9000, true],
        ['ships', [60.1, 24.9, 250000], 10000, true],
        ['chokepoints', [20.0, 40.0, 16000000], 8000, true],
        ['threed', [37.795, -122.40, 2500], 9000, false],
        ['contours', [36.1, -112.1, 12000], 8000, false],
        ['slope', [36.1, -112.1, 12000], 8000, false],
        ['sun-shadow', [40.75, -73.98, 3000], 6000, false],
        ['satellite-scenes', [37.77, -122.42, 60000], 9000, false],
        ['measure', [39.0, -98.0, 4000000], 3000, false],
        ['layer-health', [39.0, -98.0, 4000000], 4000, false],
        ['coverage', [39.0, -98.0, 5000000], 6000, true],
        ['healthy', [33.75, -84.39, 60000], 5000, false],
        ['live-video', [33.75, -84.39, 60000], 5000, false],
        ['favorites', [33.75, -84.39, 60000], 4000, false],
        ['areas', [33.75, -84.39, 60000], 4000, false],
        ['route', [33.75, -84.39, 60000], 4000, false],
        ['select', [33.75, -84.39, 60000], 3000, false],
    ];
    const LOOKS = ['standard', 'night-ops', 'satellite'];

    const wait = function (ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); };
    const button = function (key) { return document.querySelector('[data-tg-toggle="' + key + '"]'); };
    const isOn = function (key) { const b = button(key); return !!(b && b.classList.contains('tg-toggle-on')); };
    const fly = function (where) { window.tacticalGlobe.flyTo('tg-viewport', where[0], where[1], where[2], 0); };
    const closeWindows = function () {
        document.querySelectorAll('.tg-stream-panel-close, .tg-watch-wall-close').forEach(function (b) { try { b.click(); } catch (e) { /* gone */ } });
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    };
    const setSwitch = async function (key, on) {
        const b = button(key);
        if (!b || isOn(key) === on) return !!b;
        b.click();
        for (let i = 0; i < 20 && isOn(key) !== on; i++) await wait(100);
        return true;
    };
    const lookButton = function (look) {
        return Array.from(document.querySelectorAll('.tg-look-option')).find(function (b) {
            return (b.getAttribute('aria-label') || '').toLowerCase().replace(/\s+/g, '-') === look;
        });
    };
    const rowCount = function (key) {
        const b = button(key);
        const count = b && b.querySelector('.tg-row-count');
        const hint = b && b.querySelector('.tg-row-hint');
        return (count ? count.textContent.trim() : '') + (hint && hint.textContent.trim() ? ' ' + hint.textContent.trim() : '');
    };

    window.__owTour = async function (host, only) {
        const report = [];
        const initial = Array.from(document.querySelectorAll('[data-tg-toggle]'))
            .filter(function (b) { return b.classList.contains('tg-toggle-on'); }).map(function (b) { return b.dataset.tgToggle; });
        const initialLook = (document.querySelector('.tg-look-option.is-active') || {}).getAttribute
            ? document.querySelector('.tg-look-option.is-active').getAttribute('aria-label').toLowerCase().replace(/\s+/g, '-') : null;
        const shell = document.querySelector('.tg-shell');
        host.log('start; on at start: ' + initial.join(', '));
        for (const key of initial) await setSwitch(key, false);
        closeWindows();

        if (!only || only.indexOf('looks') >= 0) {
            for (const look of LOOKS) {
                const b = lookButton(look);
                if (!b) { report.push('look ' + look + ': NO BUTTON'); continue; }
                b.click();
                fly([25, 10, 18000000]); await wait(6000); await host.shot('look-' + look + '-orbit');
                fly([40.75, -73.98, 8000]); await wait(7000); await host.shot('look-' + look + '-city');
                report.push('look ' + look + ': shot');
            }
            const night = lookButton('night-ops'); if (night) night.click();
            // Night mode: somewhere the sun is down now (the page decides from the view centre).
            for (const lon of [0, 60, 120, 180, -120, -60]) {
                fly([30, lon, 3000000]); await wait(2500);
                if (shell && shell.dataset.owNight === 'night') break;
            }
            await wait(2000);
            await host.shot('night-' + (shell ? shell.dataset.owNight : 'unknown'));
            report.push('night: ' + (shell ? shell.dataset.owNight : 'unknown'));
        }

        // 'stress' (only when asked for by name): long flies back and forth with the cameras drawn, the
        // pattern both Mac crashes in MKQuadTrie insert followed (27-11-26).
        if (only && only.indexOf('stress') >= 0) {
            const places = [[25, 10, 18000000], [40.75, -73.98, 3000], [33.75, -84.39, 150000], [20, 120, 16000000], [37.8, -122.4, 8000], [51.5, -0.12, 40000]];
            const busy = ['incidents', 'conflict', 'world-news', 'hazards', 'fires', 'aircraft'];
            for (const key of busy) await setSwitch(key, true);
            for (let i = 0; i < 24; i++) {
                fly(places[i % places.length]);
                // Switch one busy layer off and on again each time: whole layers added and removed.
                const key = busy[i % busy.length];
                await setSwitch(key, false);
                await wait(800);
                await setSwitch(key, true);
                await wait(2500 + (i % 3) * 700);
                host.log('stress ' + (i + 1) + '/24');
            }
            for (const key of busy) await setSwitch(key, false);
            report.push('stress: 24 flies with ' + busy.join(', ') + ' on, still running');
        }

        let index = 0;
        for (const step of STEPS) {
            const key = step[0];
            index++;
            if (only && only.indexOf(key) < 0) continue;
            const name = String(index).padStart(2, '0') + '-' + key;
            // Cameras have no switch: they're always drawn.
            if (key === 'cameras') {
                fly(step[1]); await wait(step[2]); await host.shot(name);
                const opened = await host.select(key); await wait(2500);
                const panel = document.querySelector('.tg-stream-panel');
                if (opened && panel) await host.shot(name + '-window');
                report.push('cameras: always on; window: ' + (opened && panel ? 'opened' : 'NONE OPENED'));
                closeWindows(); await wait(500);
                continue;
            }
            // The inspector's tabs (Explore, Directions, Saved, Areas) carry data-tg-toggle too.
            const tab = button(key);
            if (tab && tab.getAttribute('role') === 'tab') {
                fly(step[1]); tab.click(); await wait(step[2]);
                await host.shot(name);
                report.push(key + ' (tab ' + tab.textContent.trim() + '): ' + (tab.getAttribute('aria-selected') === 'true' ? 'selected' : 'DID NOT SELECT'));
                continue;
            }
            // contours/slope appear under 3D once it's on, live flow under city traffic (with a TomTom key).
            const parent = key === 'contours' || key === 'slope' ? 'threed' : key === 'live-flow' ? 'city-traffic' : null;
            if (parent) { await setSwitch(parent, true); await wait(1500); }
            if (!button(key)) {
                report.push(key + ': NO SWITCH ON THIS APP' + (parent ? ' (with ' + parent + ' on)' : ''));
                if (parent) await setSwitch(parent, false);
                continue;
            }
            fly(step[1]);
            await wait(1500);
            const turned = await setSwitch(key, true);
            await wait(step[2]);
            await host.shot(name);
            let line = key + ': ' + (turned && isOn(key) ? 'on' : 'DID NOT TURN ON') + (rowCount(key) ? ' (' + rowCount(key) + ')' : '');
            if (step[3]) {
                const opened = await host.select(key);
                await wait(2000);
                const panel = document.querySelector('.tg-stream-panel, .tg-incident-panel');
                if (opened && panel) {
                    await host.shot(name + '-window');
                    line += '; window: ' + ((panel.querySelector('.tg-stream-panel-header') || panel).textContent || '').trim().slice(0, 60);
                } else {
                    line += '; window: NONE OPENED';
                }
                closeWindows();
                await wait(500);
            }
            await setSwitch(key, false);
            if (parent) await setSwitch(parent, false);
            report.push(line);
            host.log(line);
            await wait(500);
        }

        for (const key of initial) await setSwitch(key, true);
        if (initialLook && lookButton(initialLook)) lookButton(initialLook).click();
        host.log('done; switches restored');
        return report;
    };
})();
