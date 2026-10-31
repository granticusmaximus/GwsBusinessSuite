// The sign-in page's fingerprint panel (Touch ID passkeys added 2026-10-17; redesigned with "Approve
// on my Mac" 31-10-26). Three ways in, shown only where they work:
//   - Touch ID: a discoverable passkey on this computer (/auth/passkey/options and /login).
//   - Approve on my Mac: the Mac app's Touch ID, from any browser (/auth/mac-approval/*). The page
//     shows a number; the Mac app shows three and the person picks this one with Touch ID.
//   - In the Mac app's own web view, its native Touch ID (gws-native://signin).
// The server checks everything; this page only runs the scanner and goes where sign-in was headed.
import { get, isAvailable, isEmbeddedWebKit } from './sentinel-passkey.js?v=2';

const panel = document.getElementById('gws-touchid');
const title = document.getElementById('gws-touchid-title');
const status = document.getElementById('gws-touchid-status');
const number = document.getElementById('gws-touchid-number');
const here = document.getElementById('gws-touchid-here');
const mac = document.getElementById('gws-touchid-mac');
const app = document.getElementById('gws-touchid-app');
const cancel = document.getElementById('gws-touchid-cancel');
const divider = document.getElementById('gws-touchid-divider');
const usernameInput = document.getElementById('gws-login-username');
const returnUrl = (panel && panel.dataset.returnUrl) || '/admin';
const UsernameKey = 'gws-last-username';
const PollMs = 2000;
let idleText = '';
let waiting = null;

function remember(key, value) {
    try { localStorage.setItem(key, value); } catch { /* private mode */ }
}

function recall(key) {
    try { return localStorage.getItem(key); } catch { return null; }
}

function setState(state, heading, text) {
    panel.dataset.state = state;
    title.textContent = heading;
    status.textContent = text || '';
    const busy = state === 'scanning' || state === 'waiting' || state === 'done';
    for (const button of [here, mac]) if (button) button.disabled = busy;
    cancel.hidden = state !== 'waiting';
    number.hidden = state !== 'waiting';
}

function reset() {
    setState('idle', 'Sign in with your fingerprint', idleText);
}

async function signInHere() {
    setState('scanning', 'Touch the sensor', 'Use Touch ID on this computer.');
    try {
        const optionsResponse = await fetch('auth/passkey/options', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
        const begun = await optionsResponse.json();
        if (!optionsResponse.ok) throw new Error(begun.error || 'Touch ID sign-in is unavailable.');
        const assertion = await get(begun.options);
        const loginResponse = await fetch('auth/passkey/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ceremonyId: begun.ceremonyId, assertion, returnUrl })
        });
        const done = await loginResponse.json();
        if (!loginResponse.ok) throw new Error(done.error || 'Touch ID sign-in didn\'t work.');
        setState('done', 'Signed in', 'Opening your workspace…');
        window.location.assign(done.redirect || '/admin');
    } catch (e) {
        const cancelled = e && e.name === 'NotAllowedError';
        setState('error', cancelled ? 'Touch ID was cancelled' : 'That didn\'t work',
            cancelled ? 'Try again, approve on your Mac, or use your password.'
                : (e && e.message) || 'Touch ID sign-in didn\'t work. If you haven\'t added Touch ID for this browser yet, approve on your Mac or use your password.');
    }
}

async function approveOnMac() {
    const username = (usernameInput && usernameInput.value.trim()) || recall(UsernameKey) || '';
    if (!username) {
        setState('error', 'Who\'s signing in?', 'Enter your username below, then choose Approve on my Mac.');
        if (usernameInput) usernameInput.focus();
        return;
    }
    remember(UsernameKey, username);
    setState('scanning', 'Asking your Mac…', '');
    try {
        const response = await fetch('auth/mac-approval/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username }) });
        const started = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(response.status === 429 ? 'Too many sign-in attempts - wait a few minutes.' : started.error || 'Couldn\'t ask your Mac.');
        number.textContent = String(started.number);
        setState('waiting', 'Choose this number on your Mac', 'In the GWS app on your Mac, pick ' + started.number + ' and touch Touch ID.');
        const until = Date.now() + started.expiresInSeconds * 1000;
        waiting = { id: started.id, secret: started.secret, until };
        await poll(waiting);
    } catch (e) {
        setState('error', 'That didn\'t work', (e && e.message) || 'Couldn\'t ask your Mac.');
    }
}

async function poll(request) {
    while (waiting === request) {
        if (Date.now() > request.until) {
            waiting = null;
            setState('error', 'No answer from your Mac', 'Open the GWS app on your Mac (signed in, with Touch ID set up) and try again.');
            return;
        }
        await new Promise(resolve => setTimeout(resolve, PollMs));
        if (waiting !== request) return;
        let state = 'waiting';
        try {
            const response = await fetch('auth/mac-approval/' + request.id + '?secret=' + encodeURIComponent(request.secret));
            if (response.ok) state = (await response.json()).state;
        } catch { /* a blip: ask again */ }
        if (state === 'approved') {
            waiting = null;
            setState('done', 'Approved on your Mac', 'Opening your workspace…');
            window.location.assign('auth/mac-approval/' + request.id + '/complete?secret=' + encodeURIComponent(request.secret)
                + '&returnUrl=' + encodeURIComponent(returnUrl));
            return;
        }
        if (state === 'declined' || state === 'expired' || state === 'used') {
            waiting = null;
            setState('error', state === 'declined' ? 'Not approved' : 'That request ended',
                state === 'declined' ? 'It was declined on your Mac, or the wrong number was picked.' : 'Start again when you\'re ready.');
            return;
        }
    }
}

if (panel) {
    if (usernameInput && !usernameInput.value) usernameInput.value = recall(UsernameKey) || '';
    const form = usernameInput && usernameInput.form;
    if (form) form.addEventListener('submit', () => remember(UsernameKey, usernameInput.value.trim()));

    if (isEmbeddedWebKit()) {
        app.hidden = false;
        idleText = 'Touch the sensor to open GWS Business Suite.';
    } else {
        mac.hidden = false;
        mac.addEventListener('click', approveOnMac);
        if (await isAvailable()) {
            here.hidden = false;
            here.addEventListener('click', signInHere);
            idleText = 'Touch ID on this computer, or approve with the GWS app on your Mac.';
        } else {
            idleText = 'Approve with Touch ID in the GWS app on your Mac.';
        }
    }
    cancel.addEventListener('click', () => { waiting = null; reset(); });
    reset();
    panel.hidden = false;
    divider.hidden = false;
}
