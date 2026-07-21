const { app, BrowserWindow, shell, session } = require('electron');
const path = require('node:path');

const productionBaseUrl = 'https://admin.gwsapp.net';
const configuredBaseUrl = process.env.GWS_SERVER_URL || productionBaseUrl;
const baseUrl = validateBaseUrl(configuredBaseUrl);
const startUrl = new URL('/admin/sentinel', baseUrl).toString();

function validateBaseUrl(candidate) {
  try {
    const url = new URL(candidate);
    if (url.protocol === 'https:' || (app.isPackaged === false && url.protocol === 'http:')) {
      return url.origin;
    }
  } catch {
    // Use the production service when a development override is malformed.
  }
  return productionBaseUrl;
}

function isTrusted(candidate) {
  try {
    return new URL(candidate).origin === new URL(baseUrl).origin;
  } catch {
    return false;
  }
}

function createWindow() {
  const window = new BrowserWindow({
    width: 1440,
    height: 940,
    minWidth: 960,
    minHeight: 640,
    title: 'GWS Business Suite',
    backgroundColor: '#f7f6f3',
    autoHideMenuBar: true,
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
      spellcheck: true
    }
  });

  window.webContents.setWindowOpenHandler(({ url }) => {
    if (isTrusted(url)) window.loadURL(url);
    else if (url.startsWith('https://')) void shell.openExternal(url);
    return { action: 'deny' };
  });
  window.webContents.on('will-navigate', (event, url) => {
    if (isTrusted(url)) return;
    event.preventDefault();
    if (url.startsWith('https://')) void shell.openExternal(url);
  });
  window.webContents.on('did-fail-load', (_event, errorCode, errorDescription) => {
    if (errorCode === -3) return;
    void window.loadFile(path.join(__dirname, 'offline.html'), {
      query: { message: errorDescription, retry: startUrl }
    });
  });

  void window.loadURL(startUrl);
}

const hasLock = app.requestSingleInstanceLock();
if (!hasLock) app.quit();

app.whenReady().then(() => {
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
  createWindow();
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('second-instance', () => {
  const window = BrowserWindow.getAllWindows()[0];
  if (window) {
    if (window.isMinimized()) window.restore();
    window.focus();
  }
});

app.on('window-all-closed', () => app.quit());
