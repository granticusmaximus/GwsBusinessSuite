// "Sign in with Touch ID" on the login page (page-lock plan, added 2026-10-17): a discoverable
// passkey ceremony against /auth/passkey/options and /auth/passkey/login. The server verifies the
// answer and sets the session cookie; this page then goes where the sign-in was headed.
import { get, isAvailable, isEmbeddedWebKit } from './sentinel-passkey.js?v=2';

const panel = document.getElementById('gws-login-passkey');
const button = document.getElementById('gws-login-passkey-button');
const error = document.getElementById('gws-login-passkey-error');

async function signIn() {
    button.disabled = true;
    error.textContent = '';
    try {
        const optionsResponse = await fetch('auth/passkey/options', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
        const begun = await optionsResponse.json();
        if (!optionsResponse.ok) throw new Error(begun.error || 'Touch ID sign-in is unavailable.');
        const assertion = await get(begun.options);
        const loginResponse = await fetch('auth/passkey/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ceremonyId: begun.ceremonyId, assertion, returnUrl: panel.dataset.returnUrl || '/admin' })
        });
        const done = await loginResponse.json();
        if (!loginResponse.ok) throw new Error(done.error || 'Touch ID sign-in didn\'t work.');
        window.location.assign(done.redirect || '/admin');
    } catch (e) {
        error.textContent = e && e.name === 'NotAllowedError' ? 'Touch ID was cancelled.' : (e && e.message) || 'Touch ID sign-in didn\'t work.';
        button.disabled = false;
    }
}

if (panel && button && !isEmbeddedWebKit() && await isAvailable()) {
    panel.hidden = false;
    button.addEventListener('click', signIn);
}
