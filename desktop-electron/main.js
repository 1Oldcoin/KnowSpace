'use strict';

/**
 * Electron 主进程（原生 Electron + .NET API sidecar）。
 *
 * 职责边界：
 *  - 管理窗口、菜单与应用退出（安装包由 electron-builder 生成，见 electron-builder.yml；
 *    自动更新尚未接，见 docs/ROADMAP.md）；
 *  - 生成一次性本机 API 令牌，通过环境变量交给 API 进程，自己持有并注入请求头；
 *  - 拉起 / 等待 / 停止 .NET API（不重复实现任何业务逻辑）。
 *
 * 令牌处理原则：不写入命令行、不写入 URL、不写入日志、不写入 ready 文件、不下发给渲染进程。
 */

const { app, BrowserWindow, Menu, dialog, shell, session, ipcMain } = require('electron');
const { spawn } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { createUpdater } = require('./updater');

const TOKEN_HEADER = 'X-Api-Token';
const READY_TIMEOUT_MS = 180_000;
const HEALTH_TIMEOUT_MS = 180_000;
const SHUTDOWN_TIMEOUT_MS = 20_000;

let mainWindow = null;
let apiProcess = null;
let apiBaseUrl = null;
let apiToken = null;
let isQuitting = false;
let apiLogTail = [];
let updater = null;

function userDataRoot() {
  return process.env.MAF_DATA_ROOT
    || path.join(process.env.LOCALAPPDATA || app.getPath('appData'), 'MyFirstMafAgentData');
}

function logFile() {
  const directory = path.join(app.getPath('userData'), 'logs');
  fs.mkdirSync(directory, { recursive: true });
  return path.join(directory, 'electron-shell.log');
}

function log(message) {
  const line = `${new Date().toISOString()} ${message}`;
  try {
    fs.appendFileSync(logFile(), line + '\n');
  } catch {
    // 日志失败不应阻止应用启动。
  }
}

function resolveApiExecutable() {
  if (process.env.MAF_API_EXE) return process.env.MAF_API_EXE;

  const candidates = [
    path.join(process.resourcesPath || '', 'api', 'MyFirstMafAgent.Api.exe'),
    path.join(__dirname, '..', 'publish', 'desktop', 'MyFirstMafAgent.Api.exe'),
    path.join(__dirname, '..', 'publish', 'console', 'MyFirstMafAgent.Api.exe')
  ];

  for (const candidate of candidates) {
    if (candidate && fs.existsSync(candidate)) return candidate;
  }

  return null;
}

function rememberApiOutput(stream) {
  stream.setEncoding('utf8');
  stream.on('data', (chunk) => {
    const lines = chunk.split(/\r?\n/).filter((line) => line.trim().length > 0);
    apiLogTail = apiLogTail.concat(lines).slice(-40);
    for (const line of lines) log(`[api] ${line}`);
  });
}

function startApi(exePath, readyFile) {
  // 令牌经环境变量传入，不出现在命令行（命令行可被同机其它进程读到）。
  const environment = {
    ...process.env,
    DESKTOP_API_TOKEN: apiToken,
    DESKTOP_STRICT_CSP: '1'
  };

  log('starting API sidecar');

  const child = spawn(exePath, [
    '--urls', 'http://127.0.0.1:0',
    '--no-browser',
    '--desktop-ready-file', readyFile,
    '--desktop-data-root', userDataRoot()
  ], {
    cwd: path.dirname(exePath),
    env: environment,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe']
  });

  rememberApiOutput(child.stdout);
  rememberApiOutput(child.stderr);

  child.on('exit', (code, signal) => {
    log(`API sidecar exited (code=${code}, signal=${signal})`);
    if (!isQuitting) {
      const detail = apiLogTail.length > 0 ? `\n\n${apiLogTail.join('\n')}` : '';
      dialog.showErrorBox('知识库服务已停止', `后端进程意外退出（code=${code}）。${detail}`);
      app.exit(1);
    }
  });

  return child;
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function waitForReadyFile(readyFile) {
  const deadline = Date.now() + READY_TIMEOUT_MS;

  while (Date.now() < deadline) {
    if (isQuitting) throw new Error('应用正在退出。');

    if (fs.existsSync(readyFile)) {
      try {
        const payload = JSON.parse(fs.readFileSync(readyFile, 'utf8'));
        if (payload && typeof payload.url === 'string' && payload.url.length > 0) return payload.url;
      } catch {
        // 可能是写入过程中的半截文件，稍后重试。
      }
    }

    await sleep(200);
  }

  throw new Error('等待 API 就绪超时（ready 文件未出现）。');
}

async function waitForHealth(baseUrl) {
  const deadline = Date.now() + HEALTH_TIMEOUT_MS;

  while (Date.now() < deadline) {
    if (isQuitting) throw new Error('应用正在退出。');

    try {
      const response = await fetch(`${baseUrl}/api/health`, {
        headers: { [TOKEN_HEADER]: apiToken }
      });
      if (response.ok) return;
    } catch {
      // 端口已开但 Kestrel 尚未就绪，继续等待。
    }

    await sleep(300);
  }

  throw new Error('API 健康检查超时。');
}

function installTokenHeader(baseUrl) {
  const filter = { urls: [`${baseUrl}/*`] };
  session.defaultSession.webRequest.onBeforeSendHeaders(filter, (details, callback) => {
    details.requestHeaders[TOKEN_HEADER] = apiToken;
    callback({ requestHeaders: details.requestHeaders });
  });
}

function createWindow(baseUrl) {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 860,
    minWidth: 960,
    minHeight: 640,
    show: false,
    backgroundColor: '#f5f7f4',
    title: '知间',
    icon: path.join(__dirname, '..', 'assets', 'app.ico'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
      spellcheck: false
    }
  });

  // 外链一律交给系统浏览器，禁止在应用内导航到其它源。
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    openExternal(url);
    return { action: 'deny' };
  });

  mainWindow.webContents.on('will-navigate', (event, url) => {
    if (!url.startsWith(baseUrl)) {
      event.preventDefault();
      openExternal(url);
    }
  });

  mainWindow.webContents.on('will-attach-webview', (event) => event.preventDefault());

  mainWindow.once('ready-to-show', () => mainWindow.show());
  mainWindow.on('closed', () => { mainWindow = null; });

  mainWindow.loadURL(baseUrl);
  log(`window loaded ${baseUrl}`);
}

function openExternal(url) {
  try {
    const parsed = new URL(url);
    if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
      shell.openExternal(url);
    }
  } catch {
    // 非法 URL 直接忽略。
  }
}

function installMenu() {
  // Windows 上不再把菜单整块去掉：「检查更新」需要一个入口，
  // 否则用户只能靠启动时的自动检查，发现问题也无从手动重试。
  const template = [];

  if (process.platform === 'darwin') {
    template.push({ role: 'appMenu' });
  }

  template.push(
    {
      label: '文件',
      submenu: [
        process.platform === 'darwin' ? { role: 'close', label: '关闭窗口' } : { role: 'quit', label: '退出' }
      ]
    },
    {
      label: '帮助',
      submenu: [
        {
          label: '检查更新',
          click: () => {
            if (!updater) return;
            updater.check({ manual: true }).then((state) => {
              // 手动检查必须给反馈，否则用户会以为按钮没生效。
              if (state.phase === 'up-to-date') {
                dialog.showMessageBox(mainWindow, {
                  type: 'info',
                  title: '检查更新',
                  message: '当前已是最新版本。',
                  detail: `版本 ${app.getVersion()}`
                });
              } else if (state.phase === 'error' || state.phase === 'disabled') {
                dialog.showMessageBox(mainWindow, {
                  type: 'warning',
                  title: '检查更新',
                  message: state.error || '暂时无法检查更新。',
                  detail: '可以设置环境变量 MAF_UPDATE_URL，或在用户数据目录写 update-feed.json。'
                });
              }
            });
          }
        },
        { type: 'separator' },
        {
          label: '关于',
          click: () => {
            dialog.showMessageBox(mainWindow, {
              type: 'info',
              title: '关于知间',
              message: `知间 ${app.getVersion()}`,
              detail: `Electron ${process.versions.electron} · Chromium ${process.versions.chrome}\n数据目录：${userDataRoot()}`
            });
          }
        }
      ]
    }
  );

  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

async function stopApi() {
  if (!apiProcess || apiProcess.exitCode !== null) return;

  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 5000);
    await fetch(`${apiBaseUrl}/api/desktop/shutdown`, {
      method: 'POST',
      headers: { [TOKEN_HEADER]: apiToken },
      signal: controller.signal
    });
    clearTimeout(timer);
    log('shutdown requested');
  } catch (error) {
    log(`shutdown request failed: ${error.message}`);
  }

  const deadline = Date.now() + SHUTDOWN_TIMEOUT_MS;
  while (Date.now() < deadline) {
    if (apiProcess.exitCode !== null) {
      log('API sidecar stopped gracefully');
      return;
    }
    await sleep(150);
  }

  log('API sidecar did not stop in time, killing');
  apiProcess.kill();
}

async function bootstrap() {
  if (!app.requestSingleInstanceLock()) {
    app.quit();
    return;
  }

  app.on('second-instance', () => {
    if (!mainWindow) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
  });

  await app.whenReady();

  installMenu();

  ipcMain.handle('desktop:open-external', (_event, url) => {
    if (typeof url === 'string') openExternal(url);
  });

  ipcMain.handle('desktop:app-info', () => ({
    version: app.getVersion(),
    electron: process.versions.electron,
    platform: process.platform,
    packaged: app.isPackaged
  }));

  ipcMain.handle('desktop:update-state', () => (updater ? updater.getState() : null));
  ipcMain.handle('desktop:update-check', () => (updater ? updater.check({ manual: true }) : null));
  ipcMain.handle('desktop:update-download', () => (updater ? updater.download() : null));

  // 安装前会把 API / 便携数据库停掉再退出，这里只负责把结果回传给界面。
  ipcMain.handle('desktop:update-install', async () => {
    if (!updater) return { ok: false, error: '更新组件未加载。' };
    return await updater.installNow();
  });

  const exePath = resolveApiExecutable();
  if (!exePath) {
    dialog.showErrorBox('缺少后端服务',
      '未找到 MyFirstMafAgent.Api.exe。请先执行 scripts\\Publish-Portable.ps1，或设置 MAF_API_EXE 指向已发布的可执行文件。');
    app.exit(1);
    return;
  }

  const readyFile = path.join(app.getPath('userData'), 'api-ready.json');
  try {
    fs.rmSync(readyFile, { force: true });
  } catch {
    // 忽略：下次写入会覆盖。
  }

  apiToken = crypto.randomBytes(32).toString('hex');
  apiProcess = startApi(exePath, readyFile);

  try {
    apiBaseUrl = await waitForReadyFile(readyFile);
    await waitForHealth(apiBaseUrl);
    installTokenHeader(apiBaseUrl);
    createWindow(apiBaseUrl);
    log(`API ready at ${apiBaseUrl}`);

    // 自动更新：窗口就绪后再启用，避免和首屏抢资源。
    updater = createUpdater({
      log,
      getWindow: () => mainWindow,
      beforeInstall: stopApi,
      markQuitting: () => { isQuitting = true; }
    });
    updater.startPeriodicChecks();
  } catch (error) {
    log(`startup failed: ${error.message}`);
    dialog.showErrorBox('启动失败', error.message);
    isQuitting = true;
    await stopApi();
    app.exit(1);
    return;
  }

  app.on('before-quit', async (event) => {
    if (isQuitting) return;
    event.preventDefault();
    isQuitting = true;
    updater?.dispose();
    await stopApi();
    app.exit(0);
  });

  app.on('window-all-closed', () => {
    app.quit();
  });
}

bootstrap().catch((error) => {
  log(`fatal: ${error.stack || error.message}`);
  dialog.showErrorBox('启动失败', error.message);
  app.exit(1);
});
