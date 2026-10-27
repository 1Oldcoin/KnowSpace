; NSIS 自定义脚本（由 electron-builder.yml 的 nsis.include 引入）。
;
; 唯一职责：卸载/升级在删除文件之前，先停掉便携 PostgreSQL（pg0）。
; 原因：postgres.exe 由 API 进程拉起，会占住 <安装目录>\resources\api\ExternalTools\pgSQL 下的文件；
; 不停掉它，安装器删除目录时会报「拒绝访问」或留下残留文件。
;
; 说明：
;  - $INSTDIR 在卸载器里就是安装目录，后端 exe 固定在 resources\api 下（见 electron-builder.yml 的 extraResources）。
;  - 用 nsExec 插件而不是 ExecWait，避免卸载向导里闪出一个控制台窗口。
;  - 任何失败都只写日志，不阻断卸载（Args/逻辑全部在 API 侧兜底，它自己也会 catch）。
;  - 用户数据目录 %LOCALAPPDATA%\MyFirstMafAgentData 不在 $INSTDIR 下，不会被动到。

!macro myFirstMafAgentStopPortablePostgres
  DetailPrint "Stopping portable PostgreSQL..."
  nsExec::ExecToLog '"$INSTDIR\resources\api\Zhijian.Api.exe" --stop-portable-postgres'
  Pop $0
  DetailPrint "Portable PostgreSQL stop exit code: $0"
!macroend

!macro customUnInstall
  !insertmacro myFirstMafAgentStopPortablePostgres
!macroend

; 更新安装（electron-updater）会先静默运行旧版本的卸载器，同样走 customUnInstall，
; 因此升级路径也覆盖到了。
