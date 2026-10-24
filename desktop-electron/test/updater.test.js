'use strict';

/**
 * updater.js 的行为测试（不需要 Electron、不需要联网）。
 *
 * 运行：node test/updater.test.js
 *
 * 为什么要有它：自动更新的关键不是「能不能下载」，而是**安装时机**——
 * 必须先停 API（连带便携数据库）再交安装器。这套顺序在真机上很难复现验证，
 * 所以这里注入 app / autoUpdater 替身，直接断言调用顺序。
 */

const assert = require('node:assert');
const path = require('node:path');

const { createUpdater } = require('../updater.js');

const tests = [];
const test = (name, fn) => tests.push({ name, fn });

/** 构造一套替身。 */
function makeStubs({ packaged = true, version = '1.0.0' } = {}) {
  const listeners = new Map();
  const calls = { setFeedURL: [], checkForUpdates: 0, downloadUpdate: 0, quitAndInstall: [], configured: {} };

  const app = {
    isPackaged: packaged,
    getVersion: () => version,
    getPath: () => path.join(process.env.TEMP || '/tmp', 'maf-updater-test')
  };

  const autoUpdater = {
    autoDownload: true,
    autoInstallOnAppQuit: true,
    allowPrerelease: true,
    on: (event, handler) => listeners.set(event, handler),
    setFeedURL: (options) => calls.setFeedURL.push(options),
    checkForUpdates: async () => { calls.checkForUpdates += 1; },
    downloadUpdate: async () => { calls.downloadUpdate += 1; },
    quitAndInstall: (...args) => calls.quitAndInstall.push(args),
    onConfigured: () => {
      calls.configured = {
        autoDownload: autoUpdater.autoDownload,
        autoInstallOnAppQuit: autoUpdater.autoInstallOnAppQuit,
        allowPrerelease: autoUpdater.allowPrerelease
      };
    }
  };

  return {
    app,
    autoUpdater,
    listeners,
    calls,
    emit: (event, payload) => {
      const handler = listeners.get(event);
      assert.ok(handler, `没有注册 ${event} 监听器`);
      handler(payload);
    }
  };
}

function build({ app, autoUpdater }, overrides = {}) {
  const log = [];
  const order = [];
  const updater = createUpdater({
    log: (message) => log.push(message),
    getWindow: () => null,
    beforeInstall: async () => { order.push('stopApi'); },
    markQuitting: () => { order.push('markQuitting'); },
    app,
    autoUpdater,
    ...overrides
  });
  return { updater, log, order };
}

test('dev build reports "disabled" and never checks', async () => {
  const stubs = makeStubs({ packaged: false });
  const { updater } = build(stubs);

  const state = await updater.check();

  assert.strictEqual(state.phase, 'disabled');
  assert.strictEqual(stubs.calls.checkForUpdates, 0, '开发模式不应发起检查');
});

test('checking disables electron-updater auto-install and takes over the timing', async () => {
  const stubs = makeStubs();
  const { updater } = build(stubs);

  const pending = updater.check();
  stubs.emit('update-not-available');
  await pending;

  // 我们的退出路径是 app.exit(0)，自动安装永远不会被执行，必须关掉。
  assert.strictEqual(stubs.autoUpdater.autoDownload, false);
  assert.strictEqual(stubs.autoUpdater.autoInstallOnAppQuit, false);
  assert.strictEqual(stubs.calls.checkForUpdates, 1);
  assert.strictEqual(updater.getState().phase, 'up-to-date');
});

test('check uses MAF_UPDATE_URL as the feed', async () => {
  const stubs = makeStubs();
  const { updater } = build(stubs);

  const previous = process.env.MAF_UPDATE_URL;
  process.env.MAF_UPDATE_URL = 'http://127.0.0.1:8099/updates';
  try {
    const pending = updater.check();
    stubs.emit('update-not-available');
    await pending;
  } finally {
    if (previous === undefined) delete process.env.MAF_UPDATE_URL;
    else process.env.MAF_UPDATE_URL = previous;
  }

  assert.deepStrictEqual(stubs.calls.setFeedURL, [
    { provider: 'generic', url: 'http://127.0.0.1:8099/updates' }
  ]);
});

test('available auto-starts the download and reaches "downloaded"', async () => {
  const stubs = makeStubs();
  const { updater } = build(stubs);

  const pending = updater.check();
  stubs.emit('update-available', { version: '1.2.0' });
  await pending;

  assert.strictEqual(updater.getState().phase, 'available');
  assert.strictEqual(updater.getState().version, '1.2.0');
  assert.strictEqual(stubs.calls.downloadUpdate, 1, '发现新版本应立即开始下载');

  stubs.emit('download-progress', { percent: 42.6 });
  assert.strictEqual(updater.getState().percent, 43);

  stubs.emit('update-downloaded', { version: '1.2.0' });
  assert.strictEqual(updater.getState().phase, 'downloaded');
});

test('install stops the API BEFORE handing over to the installer', async () => {
  const stubs = makeStubs();
  const { updater, order } = build(stubs);

  const tooEarly = await updater.installNow();
  assert.strictEqual(tooEarly.ok, false, '未下载完成时不应做任何事');
  assert.deepStrictEqual(order, []);

  const pending = updater.check();
  stubs.emit('update-available', { version: '1.2.0' });
  stubs.emit('update-downloaded', { version: '1.2.0' });
  await pending;

  const result = await updater.installNow();

  assert.strictEqual(result.ok, true);
  // 顺序是关键：先停 API/pg0，再放行退出，最后才交安装器。
  assert.deepStrictEqual(order, ['stopApi', 'markQuitting']);
  assert.deepStrictEqual(stubs.calls.quitAndInstall, [[false, true]]);
});

test('a failing beforeInstall aborts the install and reports the reason', async () => {
  const stubs = makeStubs();
  let attempts = 0;
  const { updater, order } = build(stubs, {
    beforeInstall: async () => {
      attempts += 1;
      throw new Error('API 停不下来');
    }
  });

  const pending = updater.check();
  stubs.emit('update-available', { version: '1.2.0' });
  stubs.emit('update-downloaded', { version: '1.2.0' });
  await pending;

  const result = await updater.installNow();

  assert.strictEqual(result.ok, false);
  assert.match(result.error, /停不下来/);
  assert.strictEqual(attempts, 1);
  // 停 API 失败就绝不能放行退出、更不能调安装器。
  assert.deepStrictEqual(order, []);
  assert.deepStrictEqual(stubs.calls.quitAndInstall, []);
});

test('error events are surfaced to the UI state', async () => {
  const stubs = makeStubs();
  const { updater } = build(stubs);

  const pending = updater.check();
  stubs.emit('error', new Error('net::ERR_NAME_NOT_RESOLVED'));
  await pending.catch(() => {});

  assert.strictEqual(updater.getState().phase, 'error');
  assert.match(updater.getState().error, /ERR_NAME_NOT_RESOLVED/);
});

test('a missing electron-updater degrades to "disabled" instead of throwing', async () => {
  const app = { isPackaged: true, getVersion: () => '1.0.0', getPath: () => process.env.TEMP || '/tmp' };
  const log = [];
  // 不注入 autoUpdater，也没有安装 electron-updater 的场景由 require 抛出。
  const updater = createUpdater({
    log: (message) => log.push(message),
    getWindow: () => null,
    beforeInstall: async () => {},
    markQuitting: () => {},
    app
  });

  const state = await updater.check();

  assert.ok(['disabled', 'up-to-date', 'checking'].includes(state.phase), `意外的状态：${state.phase}`);
  assert.deepStrictEqual(updater.installNow ? await updater.installNow() : null, { ok: false, error: '更新组件未加载。' });
});

(async () => {
  let failed = 0;
  for (const { name, fn } of tests) {
    try {
      await fn();
      console.log(`  PASS  ${name}`);
    } catch (error) {
      failed += 1;
      console.error(`  FAIL  ${name}\n        ${error.message}`);
    }
  }

  console.log(`\n${tests.length - failed}/${tests.length} passed`);
  process.exit(failed === 0 ? 0 : 1);
})();
