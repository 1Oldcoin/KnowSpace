'use strict';

/**
 * 自动更新（P0-3）。
 *
 * 设计约束（每一条都来自踩过的坑）：
 *
 *  1. **更新前必须先停 API，再停便携数据库。** 安装器要替换 `resources/api` 下的文件，
 *     而便携 PostgreSQL 的 `postgres.exe` 会锁住安装目录，不停就会重演
 *     「卸载删不掉文件」的老问题。所以安装动作交给宿主注入的 `beforeInstall` 回调
 *     （它会调用 main.js 的 stopApi）。
 *  2. **不能依赖 `autoInstallOnAppQuit`。** 我们的 `before-quit` 会阻止退出并走
 *     `app.exit(0)`，那条路径**不会**执行安装器。因此只在用户确认后显式
 *     `quitAndInstall()`，并在此之前把 `isQuitting` 置真。
 *  3. **开发模式不检查。** `app.isPackaged=false` 时 electron-updater 会抛错，
 *     直接跳过并说明原因，免得开发时满屏红字。
 *  4. **更新源可覆盖。** 安装包里写死的是一个占位地址；运行时可用 `MAF_UPDATE_URL`
 *     （或用户数据目录下的 `update-feed.json`）指向自建静态目录 / 内网发布点，
 *     这样同一个安装包能服务不同部署环境。
 *  5. **依赖可注入。** `app` 与 `autoUpdater` 都可以从参数传入，默认才去 require。
 *     这样 `test/updater.test.js` 能验证「先停 API 再装」的顺序，
 *     而不必真的装一个 Electron、也不必联网。
 */

const fs = require('node:fs');
const path = require('node:path');

/** 启动后延迟检查，避免和「拉起 API + 首屏渲染」抢 IO。 */
const FIRST_CHECK_DELAY_MS = 20_000;
/** 之后每 6 小时复查一次。 */
const RECHECK_INTERVAL_MS = 6 * 60 * 60 * 1000;

/**
 * @param {object} options
 * @param {(message: string) => void} options.log
 * @param {() => (object | null)} options.getWindow 取当前窗口（可能为 null）
 * @param {() => Promise<void>} options.beforeInstall 停 API / 停数据库（幂等）
 * @param {() => void} options.markQuitting 让 before-quit 不再拦截退出
 * @param {object} [options.app] 注入 Electron app（测试用）
 * @param {object} [options.autoUpdater] 注入 electron-updater 实例（测试用）
 */
function createUpdater({
  log,
  getWindow,
  beforeInstall,
  markQuitting,
  app: injectedApp,
  autoUpdater: injectedAutoUpdater
}) {
  const hostApp = injectedApp || require('electron').app;

  const state = {
    phase: 'idle',           // idle | checking | available | downloading | downloaded | up-to-date | error | disabled
    currentVersion: hostApp.getVersion(),
    version: null,
    releaseNotes: null,
    percent: 0,
    error: null,
    feedUrl: null
  };

  let autoUpdater = null;
  let timer = null;
  let installing = false;

  function broadcast() {
    const window = getWindow();
    if (!window || (window.isDestroyed && window.isDestroyed())) return;
    try {
      window.webContents.send('desktop:update-status', { ...state });
    } catch (error) {
      log(`update status broadcast failed: ${error.message}`);
    }
  }

  function setState(patch) {
    Object.assign(state, patch);
    broadcast();
  }

  /**
   * 解析更新源：
   *   1. 环境变量 MAF_UPDATE_URL（最常用：CI/部署时指定）
   *   2. 用户数据目录 update-feed.json 里的 { "url": "..." }（允许用户自己改，不必重打包）
   *   3. 安装包里 electron-builder.yml 写入的默认值（由 electron-updater 读取 app-update.yml）
   */
  function resolveFeedUrl() {
    const fromEnvironment = (process.env.MAF_UPDATE_URL || '').trim();
    if (fromEnvironment) return fromEnvironment;

    try {
      const overridePath = path.join(hostApp.getPath('userData'), 'update-feed.json');
      if (fs.existsSync(overridePath)) {
        const payload = JSON.parse(fs.readFileSync(overridePath, 'utf8'));
        if (payload && typeof payload.url === 'string' && payload.url.trim()) return payload.url.trim();
      }
    } catch (error) {
      log(`update-feed.json unreadable: ${error.message}`);
    }

    return null;
  }

  function loadUpdater() {
    if (autoUpdater) return autoUpdater;

    if (injectedAutoUpdater) {
      autoUpdater = injectedAutoUpdater;
    } else {
      // 延迟 require：没装 electron-updater（例如纯开发机）时不该让主进程启动失败。
      // eslint-disable-next-line global-require
      const { autoUpdater: instance } = require('electron-updater');
      autoUpdater = instance;
    }

    // 由我们接管安装时机：默认值会在退出时自动装，而我们的退出路径是 app.exit(0)。
    autoUpdater.autoDownload = false;
    autoUpdater.autoInstallOnAppQuit = false;
    autoUpdater.allowPrerelease = false;

    autoUpdater.on('checking-for-update', () => {
      log('update: checking');
      setState({ phase: 'checking', error: null });
    });

    autoUpdater.on('update-available', (info) => {
      log(`update: available ${info && info.version}`);
      setState({
        phase: 'available',
        version: info && info.version ? info.version : null,
        releaseNotes: info && info.releaseNotes ? String(info.releaseNotes) : null
      });

      // 立刻开始下载：用户点「安装」时通常已经下好了，等待感更短。
      Promise.resolve(autoUpdater.downloadUpdate()).catch((error) => {
        log(`update: download failed ${error.message}`);
        setState({ phase: 'error', error: error.message });
      });
    });

    autoUpdater.on('update-not-available', () => {
      log('update: up to date');
      setState({ phase: 'up-to-date', version: null, percent: 0 });
    });

    autoUpdater.on('download-progress', (progress) => {
      setState({
        phase: 'downloading',
        percent: progress && typeof progress.percent === 'number' ? Math.round(progress.percent) : 0
      });
    });

    autoUpdater.on('update-downloaded', (info) => {
      log(`update: downloaded ${info && info.version}`);
      setState({ phase: 'downloaded', version: info && info.version ? info.version : state.version, percent: 100 });
    });

    autoUpdater.on('error', (error) => {
      log(`update: error ${error && error.message}`);
      setState({ phase: 'error', error: error ? String(error.message || error) : '未知错误' });
    });

    return autoUpdater;
  }

  async function check({ manual = false } = {}) {
    if (!hostApp.isPackaged) {
      setState({ phase: 'disabled', error: '开发模式下不检查更新（用安装版验证）。' });
      return { ...state };
    }

    let updater;
    try {
      updater = loadUpdater();
    } catch (error) {
      log(`update: electron-updater missing (${error.message})`);
      setState({ phase: 'disabled', error: '当前安装包未包含更新组件。' });
      return { ...state };
    }

    const feedUrl = resolveFeedUrl();
    if (feedUrl) {
      state.feedUrl = feedUrl;
      updater.setFeedURL({ provider: 'generic', url: feedUrl });
    }

    try {
      await updater.checkForUpdates();
    } catch (error) {
      // 未配置更新源时这里必然失败：不该弹窗骚扰，只记录并在界面里显示。
      log(`update: check failed ${error.message}`);
      setState({ phase: 'error', error: error.message, manual });
    }

    return { ...state };
  }

  /**
   * 用户点「重启并安装」。
   *
   * 顺序很关键：先停 API（同时停便携数据库），再放行退出，最后交给安装器。
   */
  async function installNow() {
    if (!autoUpdater) return { ok: false, error: '更新组件未加载。' };
    if (state.phase !== 'downloaded') return { ok: false, error: '更新尚未下载完成。' };
    if (installing) return { ok: false, error: '正在安装。' };

    installing = true;
    try {
      log('update: stopping API before install');
      await beforeInstall();
      markQuitting();
      log('update: quitAndInstall');
      autoUpdater.quitAndInstall(false, true);
      return { ok: true };
    } catch (error) {
      installing = false;
      log(`update: install failed ${error.message}`);
      return { ok: false, error: error.message };
    }
  }

  /** 只下载不安装（用户选择「稍后提醒我」，或从界面手动触发）。 */
  async function download() {
    if (!autoUpdater) return { ok: false, error: '更新组件未加载。' };
    try {
      await autoUpdater.downloadUpdate();
      return { ok: true };
    } catch (error) {
      return { ok: false, error: error.message };
    }
  }

  function startPeriodicChecks() {
    if (!hostApp.isPackaged) {
      state.phase = 'disabled';
      return;
    }

    timer = setTimeout(() => {
      check().catch((error) => log(`update: scheduled check failed ${error.message}`));
      timer = setInterval(() => {
        check().catch((error) => log(`update: periodic check failed ${error.message}`));
      }, RECHECK_INTERVAL_MS);
      if (timer.unref) timer.unref();
    }, FIRST_CHECK_DELAY_MS);

    if (timer.unref) timer.unref();
  }

  function dispose() {
    if (timer) {
      clearTimeout(timer);
      clearInterval(timer);
      timer = null;
    }
  }

  return { check, download, installNow, startPeriodicChecks, dispose, getState: () => ({ ...state }) };
}

module.exports = { createUpdater };
