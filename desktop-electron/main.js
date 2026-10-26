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
const { pathToFileURL } = require('node:url');
const { createUpdater } = require('./updater');

const TOKEN_HEADER = 'X-Api-Token';
const READY_TIMEOUT_MS = 180_000;
const HEALTH_TIMEOUT_MS = 180_000;
const SHUTDOWN_TIMEOUT_MS = 20_000;
/**
 * 启动画面延迟出现的时间。
 *
 * 后端已经在后台跑（关窗不关服务）时，从双击到窗口出现只有几百毫秒；这种情况下再闪一个
 * 启动画面反而是噪音。所以画面先建好但按住不发，超过这个时间主窗口还没就绪才显示；
 * 正常冷启动（要拉起数据库）一定超过，用户看到的是"立刻有反馈"。
 */
const SPLASH_DELAY_MS = 180;

let mainWindow = null;
let splashWindow = null;
let splashShowTimer = null;
let splashClosed = false;
/** 最后一次设置的状态文案：页面加载完成后用它补写（见 createSplash）。 */
let lastSplashPhase = '';
let apiProcess = null;
let apiBaseUrl = null;
let apiToken = null;
let apiOwned = false;
let isQuitting = false;
let updater = null;
/** 进程启动时刻：启动画面与日志用它算「用户等了多久」。 */
const startupBeganAt = Date.now();

/**
 * 运行时发现文件与共享令牌，路径与 API 侧 DesktopRuntimeFile 必须一致：
 * {数据根}/runtime/desktop.json + {数据根}/runtime/token。
 */
function runtimeDirectory() {
  return path.join(userDataRoot(), 'runtime');
}

function runtimeInfoPath() {
  return path.join(runtimeDirectory(), 'desktop.json');
}

function runtimeTokenPath() {
  return path.join(runtimeDirectory(), 'token');
}

function readJsonFile(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

function prefsPath() {
  return path.join(app.getPath('userData'), 'desktop-prefs.json');
}

/** 界面里「关闭所有服务」对应 stopBackendOnExit；默认 false：关窗口不动后台。 */
function readPrefs() {
  const stored = readJsonFile(prefsPath()) ?? {};
  return { stopBackendOnExit: stored.stopBackendOnExit === true };
}

function writePrefs(patch) {
  const next = { ...readPrefs(), ...(patch ?? {}) };
  try {
    fs.mkdirSync(path.dirname(prefsPath()), { recursive: true });
    fs.writeFileSync(prefsPath(), JSON.stringify(next, null, 2));
  } catch (error) {
    log(`write prefs failed: ${error.message}`);
  }
  return next;
}

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
    // 关窗口不关后台：子进程必须脱离父进程的管道与控制台，
    // 否则父进程一退出它就会收到 EPIPE。日志由 API 自己写文件。
    detached: true,
    stdio: 'ignore'
  });

  child.on('exit', (code, signal) => {
    log(`API sidecar exited (code=${code}, signal=${signal})`);
    if (isQuitting) return;
    dialog.showErrorBox('知识库服务已停止',
      `后端进程意外退出（code=${code}）。日志：${path.join(userDataRoot(), 'logs')}`);
    app.exit(1);
  });

  // 让本进程可以退出而不等待子进程；后台服务的生命周期不挂在窗口上。
  child.unref();

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
        if (payload && typeof payload.url === 'string' && payload.url.length > 0) {
          log(`ready file detected after ${Date.now() - startupBeganAt}ms`);
          return payload.url;
        }
      } catch {
        // 可能是写入过程中的半截文件，稍后重试。
      }
    }

    // 60ms 而不是 200ms：文件出现到我们看见它，平均白等 100ms，而每次 stat 只要几十微秒。
    await sleep(60);
  }

  throw new Error('等待 API 就绪超时（ready 文件未出现）。');
}

async function waitForHealth(baseUrl) {
  const deadline = Date.now() + HEALTH_TIMEOUT_MS;

  while (Date.now() < deadline) {
    if (isQuitting) throw new Error('应用正在退出。');

    try {
      const response = await fetch(`${baseUrl}/api/health`, {
        headers: apiToken ? { [TOKEN_HEADER]: apiToken } : {}
      });
      if (response.ok) {
        log(`health ok after ${Date.now() - startupBeganAt}ms`);
        return;
      }
    } catch {
      // 端口已开但 Kestrel 尚未就绪，继续等待。
    }

    // ready 文件已经写过，健康检查基本只会失败一次（端口刚绑定），间隔别设太大。
    await sleep(120);
  }

  throw new Error('API 健康检查超时。');
}

function installTokenHeader(baseUrl) {
  // 接管已有后台时可能没有令牌（命令行 / 服务方式启动），此时不要发一个空头。
  if (!apiToken) return;

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

  // 浏览器兜底下载（例如导出知识库包）也要让用户自己选目录：
  // 不接管的话 Electron 会默默丢进系统下载目录，用户既没选过、也找不到。
  session.defaultSession.on('will-download', (_event, item) => {
    const target = dialog.showSaveDialogSync(mainWindow, {
      title: '保存文件',
      defaultPath: path.join(app.getPath('downloads'), item.getFilename()),
      buttonLabel: '保存'
    });

    if (!target) {
      item.cancel();
      return;
    }

    item.setSavePath(target);
    item.once('done', (_doneEvent, state) => log(`download ${item.getFilename()} -> ${target} (${state})`));
  });

  mainWindow.once('ready-to-show', () => {
    if (mainWindow) mainWindow.show();
    // 主窗口一出来就把启动画面收掉：两个窗口同时存在会抢焦点。
    closeSplash();
    log(`main window shown at ${Date.now() - startupBeganAt}ms`);
  });
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

/**
 * 启动画面。
 *
 * 为什么需要：真正的首屏要等「.NET 主机起来 + 便携数据库就绪 + 建表检查完成 + Kestrel 监听」，
 * 冷启动是秒级。这段时间里如果什么都不画，用户会以为双击没生效、再点一次（于是变成
 * 「程序已经在运行」的困惑）。启动画面的价值不是装饰，而是**立刻证明点击被接收到**，
 * 并且用一句文案说明现在卡在哪一步。
 *
 * 实现取最小依赖：一个本地 HTML（不联网、不加载字体、无 JS 依赖），文案由主进程用
 * `executeJavaScript` 直接写进 DOM——这样 splash 页面不需要 preload，也不需要开
 * nodeIntegration。
 */
function createSplash() {
  if (splashWindow || splashClosed) return;

  splashWindow = new BrowserWindow({
    width: 380,
    height: 250,
    frame: false,
    resizable: false,
    movable: true,
    maximizable: false,
    minimizable: false,
    fullscreenable: false,
    center: true,
    show: false,
    backgroundColor: '#f4f8f4',
    skipTaskbar: false,
    title: '知间',
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      spellcheck: false
    }
  });

  splashWindow.once('ready-to-show', () => {
    // 先按住不发：快路径（接管已在跑的后端）不该看到闪一下的画面。
    splashShowTimer = setTimeout(() => {
      splashShowTimer = null;
      if (splashWindow && !splashWindow.isDestroyed() && !mainWindow?.isVisible()) {
        splashWindow.show();
        log(`splash shown at ${Date.now() - startupBeganAt}ms`);
      }
    }, SPLASH_DELAY_MS);
  });

  splashWindow.on('closed', () => {
    splashWindow = null;
  });

  splashWindow.loadFile(path.join(__dirname, 'splash.html'));

  splashWindow.webContents.on('did-finish-load', () => {
    // 页面加载完成后补写版本号，并把「最后一次想显示的状态」重新贴上去：
    // 在 did-finish-load 之前调用的 setSplashPhase 会因为页面还没就绪而被丢弃。
    setSplashVersion(`版本 ${app.getVersion()}`);
    if (lastSplashPhase) setSplashPhase(lastSplashPhase);
    applySplashGif();
  });
}

/** 允许用一张 gif 顶掉内置图标动画：把 splash.gif 放到 desktop-electron\ 下即可。 */
function applySplashGif() {
  if (!splashWindow || splashWindow.isDestroyed()) return;

  const gifPath = path.join(__dirname, 'splash.gif');
  if (!fs.existsSync(gifPath)) return;

  const href = pathToFileURL(gifPath).href;
  splashWindow.webContents
    .executeJavaScript(
      `(() => {
        const image = document.getElementById('splash-gif');
        if (!image) return;
        image.src = ${JSON.stringify(href)};
        image.hidden = false;
        document.body.classList.add('with-gif');
      })()`
    )
    .catch((error) => log(`splash gif failed: ${error.message}`));
}

/** 更新启动画面上的状态文案；画面不在（已关/未建）时静默跳过。 */
function setSplashPhase(text) {
  lastSplashPhase = text;
  if (!splashWindow || splashWindow.isDestroyed()) return;

  splashWindow.webContents
    .executeJavaScript(
      `(() => {
        const phase = document.getElementById('phase');
        if (phase) phase.textContent = ${JSON.stringify(text)};
      })()`
    )
    .catch(() => {
      // 页面还没加载完时执行会失败，忽略即可：did-finish-load 会用 lastSplashPhase 补上。
    });
}

/** 右下角的版本号。与状态文案分开写，避免互相覆盖。 */
function setSplashVersion(text) {
  if (!splashWindow || splashWindow.isDestroyed()) return;

  splashWindow.webContents
    .executeJavaScript(
      `(() => {
        const version = document.getElementById('version');
        if (version) version.textContent = ${JSON.stringify(text)};
      })()`
    )
    .catch(() => { });
}

/** 关闭启动画面：先淡出再销毁，避免生硬地"啪"一下消失。 */
function closeSplash() {
  if (splashShowTimer) {
    clearTimeout(splashShowTimer);
    splashShowTimer = null;
  }
  if (!splashWindow || splashWindow.isDestroyed()) {
    splashClosed = true;
    return;
  }

  const window = splashWindow;
  splashWindow = null;
  splashClosed = true;

  window.webContents
    .executeJavaScript(`document.documentElement.classList.add('leaving')`)
    .catch(() => { })
    .finally(() => {
      setTimeout(() => {
        if (!window.isDestroyed()) window.destroy();
      }, 200);
    });
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
  if (!apiBaseUrl) return;

  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 5000);
    await fetch(`${apiBaseUrl}/api/desktop/shutdown`, {
      method: 'POST',
      headers: apiToken ? { [TOKEN_HEADER]: apiToken } : {},
      signal: controller.signal
    });
    clearTimeout(timer);
    log('shutdown requested');
  } catch (error) {
    log(`shutdown request failed: ${error.message}`);
  }

  // 后台可能不是本窗口拉起的：那种情况下后端自己会退，窗口不等它。
  if (!apiOwned || !apiProcess || apiProcess.exitCode !== null) return;

  const deadline = Date.now() + SHUTDOWN_TIMEOUT_MS;
  while (Date.now() < deadline) {
    if (apiProcess.exitCode !== null) {
      log('API sidecar stopped gracefully');
      return;
    }
    await sleep(150);
  }

  log('API sidecar did not stop in time, killing');
  try {
    apiProcess.kill();
  } catch (error) {
    log(`kill failed: ${error.message}`);
  }
}

/**
 * 探测某个地址上是否有活着的后端。
 * /api/desktop/info 只在回环来源上应答，所以这里能通基本就能确定是本机后端。
 */
async function probeBackend(url, token) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 3000);

  try {
    const response = await fetch(`${url}/api/desktop/info`, {
      headers: token ? { [TOKEN_HEADER]: token } : {},
      signal: controller.signal
    });
    if (!response.ok) return null;
    return await response.json();
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

/**
 * 后台服务可能早就以命令行 / 系统服务的方式跑着了。
 * 这时读发现文件、探活一次，通了就直接接管——绝不再拉起第二个后端
 * （两个实例会抢同一个便携 PostgreSQL、同一份数据目录，还会抢同一个端口）。
 */
async function attachExistingBackend() {
  const info = readJsonFile(runtimeInfoPath());
  if (!info || typeof info.url !== 'string' || !info.url.startsWith('http://127.0.0.1:')) return null;

  let token = null;
  try {
    token = (fs.readFileSync(runtimeTokenPath(), 'utf8') ?? '').trim() || null;
  } catch {
    // 命令行 / 服务方式启动时没有令牌文件（也就不需要令牌），属正常情况。
    token = null;
  }

  const probe = await probeBackend(info.url, token);
  if (!probe) {
    log(`runtime file points at ${info.url} but nothing answered; will start our own backend`);
    return null;
  }

  apiBaseUrl = info.url;
  apiToken = token;
  apiOwned = false;
  log(`attached to running API at ${info.url} (pid=${probe.processId})`);
  return info.url;
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

  // 后台服务与退出行为（设置页「后台服务」区块用）。
  ipcMain.handle('desktop:backend-state', () => ({
    mode: apiOwned ? 'spawned' : 'attached',
    processId: apiProcess?.pid ?? null,
    stopBackendOnExit: readPrefs().stopBackendOnExit
  }));

  ipcMain.handle('desktop:prefs-get', () => readPrefs());
  ipcMain.handle('desktop:prefs-set', (_event, patch) => writePrefs(patch));

  // 安装前会把 API / 便携数据库停掉再退出，这里只负责把结果回传给界面。
  ipcMain.handle('desktop:update-install', async () => {
    if (!updater) return { ok: false, error: '更新组件未加载。' };
    return await updater.installNow();
  });

  const readyFile = path.join(app.getPath('userData'), 'api-ready.json');
  try {
    fs.rmSync(readyFile, { force: true });
  } catch {
    // 忽略：下次写入会覆盖。
  }

  try {
    // 启动画面先建：它是文件页，不依赖后端，~100ms 就能画出来。
    // 顺序很重要——先给用户反馈，再去等后端，而不是反过来。
    createSplash();
    setSplashPhase('正在准备运行环境…');

    // 先看有没有已经在跑的后台服务；有就接管，没有才自己拉起。
    if (await attachExistingBackend()) {
      setSplashPhase('已连接到正在运行的知识库服务…');
    } else {
      const exePath = resolveApiExecutable();
      if (!exePath) {
        closeSplash();
        dialog.showErrorBox('缺少后端服务',
          '未找到 MyFirstMafAgent.Api.exe。请先执行 scripts\\Publish-Portable.ps1，或设置 MAF_API_EXE 指向已发布的可执行文件。');
        app.exit(1);
        return;
      }

      apiToken = crypto.randomBytes(32).toString('hex');
      apiOwned = true;
      setSplashPhase('正在启动知识库服务…');
      apiProcess = startApi(exePath, readyFile);
      apiBaseUrl = await waitForReadyFile(readyFile);
      setSplashPhase('正在校验服务状态…');
    }

    await waitForHealth(apiBaseUrl);
    setSplashPhase('正在载入界面…');
    installTokenHeader(apiBaseUrl);
    createWindow(apiBaseUrl);
    log(`API ready at ${apiBaseUrl} (${apiOwned ? 'spawned' : 'attached'}) after ${Date.now() - startupBeganAt}ms`);

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
    closeSplash();
    dialog.showErrorBox('启动失败', error.message);
    isQuitting = true;
    if (apiOwned) await stopApi();
    app.exit(1);
    return;
  }

  app.on('before-quit', async (event) => {
    if (isQuitting) return;
    event.preventDefault();
    isQuitting = true;
    updater?.dispose();

    // 默认关窗口不关后台：用户可能把它当常驻服务用。
    // 只有设置页勾了「关闭所有服务」才让后端自己退出（含它启动的便携数据库）。
    if (readPrefs().stopBackendOnExit) {
      await stopApi();
    } else {
      log('leaving backend running (stopBackendOnExit=false)');
    }

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
