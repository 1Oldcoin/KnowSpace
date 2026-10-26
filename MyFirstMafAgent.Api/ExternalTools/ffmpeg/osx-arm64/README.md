# ffmpeg / osx-arm64

只有处理**视频**资料才需要它。

最简单的做法是 `brew install ffmpeg`，让程序回落到 PATH 里的 `ffmpeg`，本目录保持为空即可。

若要随包分发：从 <https://evermeet.cx/ffmpeg/> 下载 Apple Silicon 版本，
把 `ffmpeg`（如需截图再加 `ffprobe`）放进本目录并 `chmod +x`。
