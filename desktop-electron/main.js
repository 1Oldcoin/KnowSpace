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
/**
 * 用户看到的产品名。与 electron-builder.yml 的 productName 保持一致。
 * 磁盘上的 exe 叫 Zhijian.exe（executableName），两者故意不同，见该文件的命名说明。
 */
const APP_DISPLAY_NAME = '知间-本地知识库';
/** 后台 sidecar 的文件名（.csproj 里的 AssemblyName，不是项目目录名）。 */
const API_EXE_NAME = 'Zhijian.Api.exe';
const READY_TIMEOUT_MS = 180_000;
const HEALTH_TIMEOUT_MS = 180_000;
const SHUTDOWN_TIMEOUT_MS = 20_000;
/**
 * 后端存活看护。
 *
 * 为什么需要：窗口在 createWindow 时就把地址固化了（loadURL、令牌头过滤器、will-navigate
 * 判定都依赖它），而桌面版默认用随机回环端口，后端一旦重启就会换端口。
 * 以前没有任何重连逻辑，于是“挂着不动一段时间就用不了”——其实后端在另一个端口上活着，
 * 只是这个窗口永远指着旧地址。
 */
const WATCHDOG_INTERVAL_MS = 15_000;
/** 连续失败这么多次才判定“后端丢了”，避免单次抖动或数据库 checkpoint 卡顿触发重连。 */
const WATCHDOG_FAILURE_THRESHOLD = 3;
/** 自动恢复的冷却时间，避免恢复失败时高频重试。 */
const RECOVERY_COOLDOWN_MS = 5_000;
/** 连续恢复失败上限：达到后弹框告知用户，而不是无限重启。 */
const RECOVERY_MAX_ATTEMPTS = 5;
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
/** 看护定时器与连续失败计数（见 WATCHDOG_* 常量）。 */
let watchdogTimer = null;
let probeFailures = 0;
/** 恢复流程互斥：同一时刻只跑一个，避免定时器与 exit 回调同时触发拉起两个后端。 */
let recoveryInFlight = null;
let lastRecoveryAt = 0;
let recoveryAttempts = 0;
/**
 * 主动停止（退出应用、安装更新）时置真。
 * 此时 child 的 exit 是预期内的，不能当成“后端崩了”去恢复，否则会重拉一个后端
 * 把安装器要替换的文件重新锁住。
 */
let intentionalStop = false;
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
    path.join(process.resourcesPath || '', 'api', API_EXE_NAME),
    path.join(__dirname, '..', 'publish', 'desktop', API_EXE_NAME),
    path.join(__dirname, '..', 'publish', 'console', API_EXE_NAME)
  ];

  for (const candidate of candidates) {
    if (candidate && fs.existsSync(candidate)) return candidate;
  }

  return null;
}

/**
 * 读取 sidecar 的 appsettings.json 里的 Server:FixedLocalPort。
 *
 * 0 / 缺省 = 随机回环端口（安全默认：端口不可预测）。
 * 设成固定端口只是「配置可以写死」的便利，不改变「只绑回环地址」的安全前提，
 * 因此这里不做任何自动广播或对外监听。
 */
function resolveFixedLocalPort(exePath) {
  const fromEnv = Number.parseInt(process.env.MAF_FIXED_LOCAL_PORT || '', 10);
  if (Number.isInteger(fromEnv) && fromEnv > 0 && fromEnv < 65536) return fromEnv;

  try {
    const settingsPath = path.join(path.dirname(exePath), 'appsettings.json');
    if (!fs.existsSync(settingsPath)) return 0;

    const settings = JSON.parse(fs.readFileSync(settingsPath, 'utf8'));
    const port = Number.parseInt(settings?.Server?.FixedLocalPort, 10);
    if (Number.isInteger(port) && port > 0 && port < 65536) {
      log(`fixed local port enabled: ${port} (only recommended on trusted single-user machines)`);
      return port;
    }
  } catch (error) {
    log(`failed to read Server:FixedLocalPort: ${error.message}`);
  }

  return 0;
}

function startApi(exePath, readyFile) {
  // 令牌经环境变量传入，不出现在命令行（命令行可被同机其它进程读到）。
  const environment = {
    ...process.env,
    DESKTOP_API_TOKEN: apiToken,
    DESKTOP_STRICT_CSP: '1'
  };

  // 默认随机端口；只有显式配置固定端口时才写死（见 resolveFixedLocalPort）。
  const fixedPort = resolveFixedLocalPort(exePath);
  const listenUrl = fixedPort > 0 ? `http://127.0.0.1:${fixedPort}` : 'http://127.0.0.1:0';

  log(`starting API sidecar on ${listenUrl}`);

  const child = spawn(exePath, [
    '--urls', listenUrl,
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
    if (apiProcess === child) apiProcess = null;
    if (isQuitting || intentionalStop) return;

    // 以前这里直接 dialog.showErrorBox + app.exit(1)：后端一崩，窗口连同未保存的输入
    // 一起被强杀。现在先尝试自动恢复（重新拉起，或接管另一个还活着的实例）。
    void recoverBackend('后端进程意外退出', { allowSpawn: true });
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

/**
 * 给发往本机后端的请求注入令牌。
 *
 * <p>
 * 关键是**只注册一次、地址动态判断**：以前的实现把 `baseUrl` 闭包进过滤器 URL，
 * 后端换了端口（重启必然换，因为默认是随机端口）之后这个过滤器就再也匹配不上，
 * 于是所有请求都缺令牌、界面直接 401/离线。现在过滤器只限定 `127.0.0.1`，
 * 具体端口在回调里跟当前的 `apiBaseUrl` 比对。
 * </p>
 *
 * <p>
 * Chrome 匹配模式不含端口（端口被当作通配），所以 `http://127.0.0.1/*` 能覆盖任意端口。
 * </p>
 */
function installTokenHeader() {
  session.defaultSession.webRequest.onBeforeSendHeaders(
    { urls: ['http://127.0.0.1/*', 'http://localhost/*'] },
    (details, callback) => {
      // apiToken 为空（接管命令行启动的实例）时不发头；
      // 也不能提前 return 不注册监听器——那样一旦后期自动恢复拉起了带令牌的新后端，
      // 就再也没机会补上注入，界面会变成全部 401。
      if (apiToken && apiBaseUrl && details.url.startsWith(apiBaseUrl)) {
        details.requestHeaders[TOKEN_HEADER] = apiToken;
      }
      callback({ requestHeaders: details.requestHeaders });
    }
  );
}

function createWindow(baseUrl) {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 860,
    minWidth: 960,
    minHeight: 640,
    show: false,
    backgroundColor: '#f5f7f4',
    title: APP_DISPLAY_NAME,
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
    // 比对当前的 apiBaseUrl 而不是启动时捕获的那个：自动恢复后地址会变，
    // 用旧值会把恢复后的正常导航也拦下来。
    if (!apiBaseUrl || !url.startsWith(apiBaseUrl)) {
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
    title: APP_DISPLAY_NAME,
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
              title: `关于 ${APP_DISPLAY_NAME}`,
              message: `${APP_DISPLAY_NAME} ${app.getVersion()}`,
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

  // 标记这是「主动停止」：期间 child 的 exit 属预期，不能触发自动恢复，
  // 否则安装器要替换的文件会被新拉起的后端重新锁住。
  intentionalStop = true;
  stopWatchdog();

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

/**
 * 进程是否还活着。
 * 用于在“探活失败”时区分「后端真的没了」与「后端还活着但一时没应答」：
 * 后者绝不能拉起第二个实例。信号 0 不做任何事，只做存在性检查；
 * EPERM 表示进程存在但无权限，同样算活着。
 */
function isProcessAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === 'EPERM';
  }
}

/**
 * 确保有一个可用的后端，返回它的地址；失败抛错。
 *
 * 顺序：先接管已经跑着的实例（可能刚被自动恢复拉起，端口与上次不同），
 * 再自己拉起。绝不并存两个后端——它们会抢同一个便携 PostgreSQL、同一份数据目录与端口。
 */
async function ensureBackend() {
  if (await attachExistingBackend()) return apiBaseUrl;

  // 运行时文件指向的进程还活着，却探活失败：可能是数据库 checkpoint / GC 卡了一下。
  // 这时**绝对不能**再拉一个后端——两个实例会抢同一个便携 PostgreSQL 与数据目录。
  // 给它一个宽限期，实在不行就报错交给上层去重试，而不是盲目 spawn。
  const recorded = readJsonFile(runtimeInfoPath());
  if (recorded && isProcessAlive(recorded.processId)) {
    log(`runtime file points at live pid=${recorded.processId} but no response; waiting for it`);
    for (let attempt = 0; attempt < 5; attempt += 1) {
      await sleep(2000);
      if (await attachExistingBackend()) return apiBaseUrl;
    }
    throw new Error(
      `已有后端进程（pid=${recorded.processId}）但不响应请求；为避免两个实例争抢数据库，不再启动第二个。`);
  }

  const exePath = resolveApiExecutable();
  if (!exePath) {
    throw new Error(`未找到 ${API_EXE_NAME}。请先执行 scripts\\Publish-Portable.ps1，或设置 MAF_API_EXE 指向已发布的可执行文件。`);
  }

  const readyFile = path.join(app.getPath('userData'), 'api-ready.json');
  try {
    fs.rmSync(readyFile, { force: true });
  } catch {
    // 忽略：下次写入会覆盖。
  }

  apiToken = crypto.randomBytes(32).toString('hex');
  apiOwned = true;
  apiProcess = startApi(exePath, readyFile);
  apiBaseUrl = await waitForReadyFile(readyFile);
  await waitForHealth(apiBaseUrl);
  return apiBaseUrl;
}

/**
 * 后端丢失后把窗口接回一个新实例。
 *
 * 存在的理由：桌面版默认监听随机回环端口，后端重启就换端口，而窗口的地址是启动时
 * 固化的。没有这一步，用户只能手动重启整个应用——这正是“挂着不动就用不了”的成因。
 *
 * @param {string} reason 日志里的原因
 * @param {{ allowSpawn?: boolean }} [options] 是否允许拉起新进程（看护发现的故障允许；exit 回调里也已允许）
 */
function recoverBackend(reason, options = {}) {
  // 同一时刻只跑一个恢复流程：看护定时器与 exit 回调可能同时报障。
  if (recoveryInFlight) return recoveryInFlight;
  if (isQuitting || intentionalStop) return Promise.resolve(false);

  recoveryInFlight = (async () => {
    const sinceLast = Date.now() - lastRecoveryAt;
    if (sinceLast < RECOVERY_COOLDOWN_MS) {
      await sleep(RECOVERY_COOLDOWN_MS - sinceLast);
    }
    lastRecoveryAt = Date.now();

    if (recoveryAttempts >= RECOVERY_MAX_ATTEMPTS) {
      log(`recovery: giving up after ${recoveryAttempts} attempts`);
      stopWatchdog();
      dialog.showErrorBox('知识库服务无法恢复',
        `${reason}。已自动重试 ${recoveryAttempts} 次仍未成功。\n\n` +
        `日志：${path.join(userDataRoot(), 'logs')}\n\n` +
        '请确认没有安全软件拦截后端进程，然后重新启动应用。');
      return false;
    }

    recoveryAttempts += 1;
    log(`recovery attempt ${recoveryAttempts}: ${reason}`);

    try {
      // 先看有没有别的实例顶上来了（例如服务方式启动的那个），有就直接接管。
      const attached = await attachExistingBackend();
      if (!attached) {
        if (!options.allowSpawn) return false;
        await ensureBackend();
      }

      // 地址可能变了：令牌头过滤器按 apiBaseUrl 动态匹配，只要更新变量即可。
      // 恢复过程可能新建了带新令牌的后端，所以这里再调一次（幂等，同过滤器会替换旧监听器）。
      probeFailures = 0;
      recoveryAttempts = 0;
      installTokenHeader();

      if (mainWindow && !mainWindow.isDestroyed()) {
        log(`recovery: reloading window onto ${apiBaseUrl}`);
        await mainWindow.loadURL(apiBaseUrl);
        mainWindow.show();
      }

      log(`recovery succeeded, backend at ${apiBaseUrl}`);
      return true;
    } catch (error) {
      log(`recovery failed: ${error.message}`);
      return false;
    } finally {
      recoveryInFlight = null;
    }
  })();

  return recoveryInFlight;
}

/** 开始看护：连续 WATCHDOG_FAILURE_THRESHOLD 次探活失败才判定后端丢失。 */
function startWatchdog() {
  stopWatchdog();

  watchdogTimer = setInterval(async () => {
    if (isQuitting || intentionalStop || recoveryInFlight) return;
    if (!apiBaseUrl) return;

    // 用 /api/desktop/info 而不是 /api/health：前者还能证明「这个端口上是我们自己的后端」，
    // 避免端口被别的程序占用时误判为健康。
    const probe = await probeBackend(apiBaseUrl, apiToken);
    if (probe) {
      probeFailures = 0;
      return;
    }

    probeFailures += 1;
    log(`watchdog: probe failed (${probeFailures}/${WATCHDOG_FAILURE_THRESHOLD})`);
    if (probeFailures >= WATCHDOG_FAILURE_THRESHOLD) {
      probeFailures = 0;
      await recoverBackend('长时间没有响应', { allowSpawn: true });
    }
  }, WATCHDOG_INTERVAL_MS);

  // 定时器不应阻止进程退出。
  watchdogTimer.unref?.();
}

function stopWatchdog() {
  if (watchdogTimer) {
    clearInterval(watchdogTimer);
    watchdogTimer = null;
  }
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

  // readyFile 的重建与清理已移进 ensureBackend（自动恢复会再次用到它），
  // 这里不再预先删除，避免两处各删一次、行为分叉。

  try {
    // 启动画面先建：它是文件页，不依赖后端，~100ms 就能画出来。
    // 顺序很重要——先给用户反馈，再去等后端，而不是反过来。
    createSplash();
    setSplashPhase('正在准备运行环境…');

    // 先看有没有已经在跑的后台服务；有就接管，没有才自己拉起。
    // 逻辑收在 ensureBackend 里，自动恢复时复用同一份，避免两处行为分叉。
    if (await attachExistingBackend()) {
      setSplashPhase('已连接到正在运行的知识库服务…');
    } else {
      setSplashPhase('正在启动知识库服务…');
      await ensureBackend();
    }

    setSplashPhase('正在校验服务状态…');
    await waitForHealth(apiBaseUrl);
    setSplashPhase('正在载入界面…');
    // 令牌头只注册一次，之后按当前 apiBaseUrl 动态匹配（见 installTokenHeader）。
    installTokenHeader();
    createWindow(apiBaseUrl);
    startWatchdog();
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
    stopWatchdog();
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
