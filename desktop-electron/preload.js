'use strict';

/**
 * Preload 只暴露明确的白名单接口。
 *
 * 注意：本机 API 令牌<b>不</b>在这里暴露。令牌由主进程注入到发往 127.0.0.1 的请求头，
 * 渲染进程既读不到也不需要它。
 */

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('desktopHost', {
  platform: process.platform,
  openExternal: (url) => ipcRenderer.invoke('desktop:open-external', url),

  /** 版本信息（设置页「关于」用）。 */
  getAppInfo: () => ipcRenderer.invoke('desktop:app-info'),

  /** 自动更新：查询状态 / 手动检查 / 手动下载 / 立即安装并重启。 */
  getUpdateState: () => ipcRenderer.invoke('desktop:update-state'),
  checkForUpdates: () => ipcRenderer.invoke('desktop:update-check'),
  downloadUpdate: () => ipcRenderer.invoke('desktop:update-download'),
  installUpdate: () => ipcRenderer.invoke('desktop:update-install'),

  /**
   * 后台服务：本窗口是自己拉起的（spawned）还是接管了已在运行的服务（attached），
   * 以及关闭程序时是否连后端一起停（stopBackendOnExit）。
   */
  getBackendState: () => ipcRenderer.invoke('desktop:backend-state'),
  getPrefs: () => ipcRenderer.invoke('desktop:prefs-get'),
  setPrefs: (patch) => ipcRenderer.invoke('desktop:prefs-set', patch),

  /**
   * 订阅更新状态推送。返回取消订阅函数，组件卸载时务必调用，
   * 否则每次重新挂载都会多一个监听器。
   */
  onUpdateStatus: (callback) => {
    const listener = (_event, state) => callback(state);
    ipcRenderer.on('desktop:update-status', listener);
    return () => ipcRenderer.removeListener('desktop:update-status', listener);
  }
});
