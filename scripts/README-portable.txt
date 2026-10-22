MyFirstMafAgent 便携版

一、启动
- 安装版（推荐）：双击 MyFirstMafAgent-win-Setup.exe 安装，
  之后从桌面或开始菜单启动「MyFirstMafAgent」。
- 桌面版文件夹：双击 MyFirstMafAgent.Desktop.exe，直接打开原生窗口。
- 控制台版：双击 MyFirstMafAgent.Api.exe，启动后自动用默认浏览器打开 http://127.0.0.1:5056。

二、安装版 vs 文件夹版
安装版会把程序装到 %LOCALAPPDATA%\MyFirstMafAgent（无需管理员权限），
数据放在 %LOCALAPPDATA%\MyFirstMafAgentData，卸载不会删除数据。
文件夹版适合绿色免安装，但**不要放在桌面 / 文档 / 下载目录**：
Windows 的「受控文件夹访问」会限制从这些目录启动的未签名程序写入用户目录，
导致便携数据库无法初始化。请放到例如 D:\Apps\MyFirstMafAgent。
目录名可以包含中文（例如 D:\软件\MyFirstMafAgent），程序会自动处理编码问题。

三、便携数据库
- 首次启动会自动释放 pg0（PostgreSQL 18 + pgvector），并自动执行
  CREATE EXTENSION IF NOT EXISTS vector。
- 程序会把 pg0 复制到数据和目录 %LOCALAPPDATA%\MyFirstMafAgentData\tools 后再运行，
  这样数据库进程不会占用程序安装目录，升级/卸载才能正常替换文件。
- 数据目录：%LOCALAPPDATA%\MyFirstMafAgentData\pgsql
- pgvector 的 PostgreSQL 安装：%USERPROFILE%\.pg0\installation
- 默认端口：55432（被占用时自动换端口）
- 如需使用外部 PostgreSQL，在 appsettings.json 的
  ConnectionStrings:KnowledgeBase 中填写连接串，便携数据库会自动跳过。
- 升级：直接运行新版 Setup.exe 覆盖安装即可（会自动停止旧版数据库），
  用户数据保留。

四、日志
- %LOCALAPPDATA%\MyFirstMafAgentData\logs
- 桌面版启动失败时会弹窗，详细堆栈写入 desktop-crash.log。
- 启动过程记录在 desktop-boot.log（每次启动覆盖），排查问题时先看这个文件。

五、常见问题
- 报 “os error 5” 或 “Access is denied”：
  文件夹版被放在了桌面/文档目录，或安全软件拦截了对
  %USERPROFILE%\.pg0 的写入。请改用安装版，或把文件夹移出受保护目录。
- 首次启动较慢（10-60 秒）：需要解压 PostgreSQL 并安装 pgvector，属正常现象。
- 桌面版窗口一片空白：先看 desktop-boot.log。正常日志里应该有
  apartment=STA，以及后面的 window created / load requested；
  若缺少，请重新安装（可能是安装不完整或 WebView2 初始化失败）。
- 桌面版需要 WebView2 Runtime（Windows 10/11 通常已自带）。
- 安装版无需管理员权限；卸载时会先停止数据库，用户数据默认保留。
