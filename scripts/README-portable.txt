MyFirstMafAgent 便携版

一、启动
- 桌面版（Electron 壳）：双击 MyFirstMafAgent.exe，由一个 Electron 窗口打开界面，
  后台自动拉起 MyFirstMafAgent.Api.exe（sidecar），退出窗口时后端一起退出。
- 控制台版：双击 MyFirstMafAgent.Api.exe，启动后自动用默认浏览器打开 http://127.0.0.1:5056。
- 数据库不需要单独启动：便携 PostgreSQL 由 MyFirstMafAgent.Api.exe 自己拉起，
  等它就绪后才开始对外提供界面/接口；API 退出时数据库跟着停。

二、目录说明
桌面版把 API 放在 resources\api 下；数据统一放在 %LOCALAPPDATA%\MyFirstMafAgentData，
卸载或升级不会删除数据。
文件夹版适合绿色免安装，但**不要放在桌面 / 文档 / 下载目录**：
Windows 的「受控文件夹访问」会限制从这些目录启动的未签名程序写入用户目录，
导致便携数据库无法初始化。请放到例如 D:\Apps\MyFirstMafAgent。
目录名可以包含中文（例如 D:\软件\MyFirstMafAgent）：此时程序会先把随包的
PostgreSQL 目录复制一份到 %LOCALAPPDATA%\MyFirstMafAgentData\pgsql 再运行，
首次启动因此多花几秒。数据目录本身仍会放在纯英文路径下（PostgreSQL 的硬性要求）。

三、便携数据库（PostgreSQL 17.9 + pgvector 0.8.7，随包分发）
- 位置：程序目录下的 ExternalTools\pgSQL\<平台>\（bin\ lib\ share\ 三块，不要拆分）。
- 首次启动会初始化一个新的数据库集群并自动执行 CREATE EXTENSION IF NOT EXISTS vector，
  通常 3~10 秒；之后启动只等数据库就绪，1~3 秒。
- 数据目录：%LOCALAPPDATA%\MyFirstMafAgentData\pgdata
  日志目录：%LOCALAPPDATA%\MyFirstMafAgentData\pglogs（postgres.log 是数据库自己的日志）
- 默认端口：55432（被占用时自动换下一个空闲端口，不需要手工改）。
- 如需使用外部 PostgreSQL，在 appsettings.json 的 ConnectionStrings:KnowledgeBase
  中填写连接串，便携数据库会自动跳过；填了但不是有效的库，启动会直接给出
  “主机名解析失败 / TCP 连不上 / 认证失败”这三类明确提示，而不是一堆堆栈。
- 升级：先退出应用（数据库会随之停止），再用新版文件覆盖程序目录即可，用户数据保留。
  安装版/升级包会自动调用 MyFirstMafAgent.Api.exe --stop-portable-postgres 停库。
- 不要手工删除或改名 %LOCALAPPDATA%\MyFirstMafAgentData\pgdata：那是你的全部数据；
  要备份就整目录复制（先退出应用）。

四、日志
- 应用日志：%LOCALAPPDATA%\MyFirstMafAgentData\logs
- 数据库日志：%LOCALAPPDATA%\MyFirstMafAgentData\pglogs\postgres.log
- Electron 壳的日志：%APPDATA%\myfirstmafagent-desktop\logs\electron-shell.log（含 API 输出尾巴）。
- 后端启动失败时窗口会弹提示，同时把堆栈写入上面的日志目录。

五、常见问题
- 报 “Access is denied” / “os error 5”：
  文件夹版被放在了桌面/文档目录，或安全软件拦截了对 %LOCALAPPDATA%\MyFirstMafAgentData
  的写入。请把文件夹移出受保护目录，或在安全软件里放行它。
- 报 “找不到可写且路径为纯英文的目录”：
  用户目录、临时目录、公共程序数据、用户主目录都写不进去（典型是安全软件全盘拦截）。
  三种解法：在安全软件里放行本程序；把程序放到普通目录（如 D:\Apps\MyFirstMafAgent）；
  或在 appsettings.json 里把 PortablePostgres:RootDirectory 指到一个已存在、可写、
  纯英文的目录（例如 D:\MyFirstMafAgentData）。
- 报 “invalid byte sequence for encoding "UTF8"” 且程序目录含中文：
  旧版本才会遇到；当前版本会自动把 PostgreSQL 目录镜像到纯英文路径。若仍出现，
  检查 %LOCALAPPDATA%\MyFirstMafAgentData 是否可写（镜像失败时会退回原目录并给出这条错误）。
- 报 “postgres.exe 正在被占用 / 文件无法覆盖”：
  升级前没退出应用。结束 MyFirstMafAgent.Api.exe 与残留的 postgres.exe，或运行
  MyFirstMafAgent.Api.exe --stop-portable-postgres，再覆盖文件。
- 提示端口被占用：程序会自动换端口，一般不需要处理；若 55432 长期被别的 PostgreSQL 占着，
  可在 appsettings.json 里改 PortablePostgres:Port。
- 安装包双击后没有反应、只留下退出码 2：
  安装包本身处在受保护目录（桌面/文档/下载）里。把它复制到 C:\Temp 之类的普通目录
  再双击即可；这是 Windows「受控文件夹访问」的限制，不是安装包损坏。
- 桌面版窗口空白 / 一直停在启动画面：先看 electron-shell.log 里有没有
  "starting API sidecar" 与 "API sidecar ready"，再看 API 日志；常见原因是
  resources\api 下的文件不完整（重新发布即可），或 ExternalTools\pgSQL 少了 bin/lib/share 中的某一块。
- 安装版无需管理员权限（也**不要**用管理员身份运行：PostgreSQL 拒绝提权令牌）；
  卸载时会先停止数据库，用户数据默认保留。
