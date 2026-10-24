MyFirstMafAgent 便携版

一、启动
- 桌面版（Electron 壳）：双击 MyFirstMafAgent.exe，由一个 Electron 窗口打开界面，
  后台自动拉起 MyFirstMafAgent.Api.exe（sidecar），退出窗口时后端一起退出。
- 控制台版：双击 MyFirstMafAgent.Api.exe，启动后自动用默认浏览器打开 http://127.0.0.1:5056。

二、目录说明
桌面版把 API 放在 resources\api 下；数据统一放在 %LOCALAPPDATA%\MyFirstMafAgentData，
卸载或升级不会删除数据。
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
- 升级：退出应用后直接用新版文件覆盖程序目录即可（数据库已先停止），
  用户数据保留。

四、日志
- %LOCALAPPDATA%\MyFirstMafAgentData\logs
- Electron 壳的日志：%APPDATA%\myfirstmafagent-desktop\logs\electron-shell.log（含 API 输出尾巴）。
- 后端启动失败时窗口会弹提示，同时把堆栈写入上面的日志目录。

五、常见问题
- 报 “os error 5” 或 “Access is denied”：
  文件夹版被放在了桌面/文档目录，或安全软件拦截了对
  %USERPROFILE%\.pg0 的写入。请改用安装版，或把文件夹移出受保护目录。
- 安装包双击后没有反应、只留下退出码 2：
  安装包本身在受保护目录（桌面/文档/下载）里运行。把它复制到 C:\Temp 之类
  的普通目录再双击即可；这是 Windows「受控文件夹访问」的限制，不是安装包损坏。
- 首次启动较慢（10-60 秒）：需要解压 PostgreSQL 并安装 pgvector，属正常现象。
- 桌面版窗口空白 / 一直停在启动画面：先看 electron-shell.log 里有没有
  "starting API sidecar" 与 "API sidecar ready"，再看 API 日志；常见原因是
  resources\api 下的文件不完整（重新发布即可）。
- 安装版无需管理员权限；卸载时会先停止数据库，用户数据默认保留。
