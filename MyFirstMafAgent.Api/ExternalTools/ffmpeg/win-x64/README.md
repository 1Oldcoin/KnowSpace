# ffmpeg / win-x64

Windows 的 `ffmpeg.exe` / `ffprobe.exe` / `ffplay.exe` 目前放在**上一级目录**
（`ExternalTools/ffmpeg/`），这是历史布局，程序会同时识别，所以本目录可以是空的。

Electron 安装包通过 `extraResources` 整体搬运 `ExternalTools`，保持现状即可。
