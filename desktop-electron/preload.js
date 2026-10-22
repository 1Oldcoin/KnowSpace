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
  openExternal: (url) => ipcRenderer.invoke('desktop:open-external', url)
});
