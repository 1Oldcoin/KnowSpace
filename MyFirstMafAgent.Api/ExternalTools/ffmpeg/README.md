# ffmpeg（视频转写 / 关键帧）按平台放置

程序启动时按当前平台查找，**按优先级**：

1. `ExternalTools/ffmpeg/{rid}/ffmpeg`（Windows 为 `ffmpeg.exe`）
2. `ExternalTools/ffmpeg/ffmpeg`（Windows 为 `ffmpeg.exe`）← 旧版单平台布局
3. PATH 里的 `ffmpeg`（**Docker 镜像推荐**：`apt-get install ffmpeg` 即可，不用随包分发）

> 只有处理**视频**资料时才需要 ffmpeg。纯文本/PDF/DOCX 知识库可以完全没有它。

| RID | 建议来源 |
| --- | --- |
| `win-x64` | 旧版放在上一级目录（`ffmpeg.exe` / `ffprobe.exe`），兼容保留 |
| `linux-x64` | <https://johnvansickle.com/ffmpeg/>（static build）或发行版包管理器 |
| `linux-arm64` | 发行版包管理器，或自行交叉编译的 static build |
| `osx-arm64` / `osx-x64` | <https://evermeet.cx/ffmpeg/> 或 `brew install ffmpeg` |

放好后记得 `chmod +x`（程序会在启动前自动补一次，但不应依赖它）。

下载（需能访问外网）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Runtime linux-x64 -Target ffmpeg
```
