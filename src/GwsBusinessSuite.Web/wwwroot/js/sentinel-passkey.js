// Page lock, Touch ID in the browser (Sentinel page-lock plan, phase 2): WebAuthn passkey
// ceremonies. The server sends WebAuthn options as JSON (base64url binary fields); this turns them
// into what navigator.credentials expects and turns the answer back into JSON for the server.

function fromBase64Url(value) {
    const base64 = value.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((value.length + 3) % 4);
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes.buffer;
}

function toBase64Url(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (const byte of bytes) binary += String.fromCharCode(byte);
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// True when this browser can use Touch ID / Face ID (a platform authenticator) for passkeys.
export async function isAvailable() {
    try {
        return !!window.PublicKeyCredential
            && await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable();
    } catch {
        return false;
    }
}

export async function create(optionsJson) {
    const options = JSON.parse(optionsJson);
    options.challenge = fromBase64Url(options.challenge);
    options.user.id = fromBase64Url(options.user.id);
    options.excludeCredentials = (options.excludeCredentials || []).map(c => ({ ...c, id: fromBase64Url(c.id) }));
    const credential = await navigator.credentials.create({ publicKey: options });
    return JSON.stringify({
        id: credential.id,
        rawId: toBase64Url(credential.rawId),
        type: credential.type,
        response: {
            attestationObject: toBase64Url(credential.response.attestationObject),
            clientDataJSON: toBase64Url(credential.response.clientDataJSON),
            transports: credential.response.getTransports ? credential.response.getTransports() : []
        },
        clientExtensionResults: credential.getClientExtensionResults ? credential.getClientExtensionResults() : {}
    });
}

export async function get(optionsJson) {
    const options = JSON.parse(optionsJson);
    options.challenge = fromBase64Url(options.challenge);
    options.allowCredentials = (options.allowCredentials || []).map(c => ({ ...c, id: fromBase64Url(c.id) }));
    const credential = await navigator.credentials.get({ publicKey: options });
    return JSON.stringify({
        id: credential.id,
        rawId: toBase64Url(credential.rawId),
        type: credential.type,
        response: {
            authenticatorData: toBase64Url(credential.response.authenticatorData),
            clientDataJSON: toBase64Url(credential.response.clientDataJSON),
            signature: toBase64Url(credential.response.signature),
            userHandle: credential.response.userHandle ? toBase64Url(credential.response.userHandle) : null
        },
        clientExtensionResults: credential.getClientExtensionResults ? credential.getClientExtensionResults() : {}
    });
}

// What this device calls the unlock a passkey uses (07-11-26: phones and tablets use passkeys too,
// in the browser or the app added to the Home Screen). short names a button, long fits a sentence.
export function unlockName() {
    const ua = navigator.userAgent || '';
    const appleTouch = /iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1);
    const phoneOrTablet = appleTouch || /Android|Mobile/.test(ua);
    const name = appleTouch ? { short: 'Face ID', long: 'Face ID or Touch ID' }
        : /Android/.test(ua) ? { short: 'Fingerprint', long: 'your fingerprint or screen lock' }
        : /Macintosh/.test(ua) ? { short: 'Touch ID', long: 'Touch ID' }
        : /Windows/.test(ua) ? { short: 'Windows Hello', long: 'Windows Hello' }
        : { short: 'Passkey', long: 'your passkey' };
    return { ...name, appleTouch, device: phoneOrTablet ? 'this device' : 'this computer' };
}

// The GWS Mac app's web view (WKWebView): WebKit without Safari's "Safari/" token. Passkeys don't
// work there, so the page offers the app's own Touch ID (a gws-native:// link the app handles).
export function isEmbeddedWebKit() {
    const ua = navigator.userAgent || '';
    // The app installed from Safari onto an iPhone or iPad's Home Screen (PWA, 07-11-26) leaves out
    // "Safari/" too, but it's Safari: passkeys work and there's no Mac app to hand over to.
    const installedWebApp = navigator.standalone === true
        || (typeof window.matchMedia === 'function' && window.matchMedia('(display-mode: standalone)').matches);
    return !installedWebApp
        && /AppleWebKit\//.test(ua) && /Macintosh|iPhone|iPad/.test(ua) && !/Safari\/|Chrome\/|Edg\//.test(ua);
}

// The Mac app tells the page when its Touch ID step is done by dispatching a "gws-native" event
// (detail: { action, ok, message }). The page then asks the server - the event itself proves nothing.
export function listenForNative(dotNetRef) {
    const handler = event => {
        try { dotNetRef.invokeMethodAsync('OnNativeResult', JSON.stringify(event.detail || {})); }
        catch { /* the circuit may be gone */ }
    };
    window.addEventListener('gws-native', handler);
    return { dispose: () => window.removeEventListener('gws-native', handler) };
}
