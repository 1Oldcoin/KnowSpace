# ffmpeg / linux-arm64

只有处理**视频**资料才需要它。

推荐用发行版包管理器（`apt-get install -y ffmpeg` / `apk add ffmpeg`），
让程序回落到 PATH 里的 `ffmpeg`，本目录保持为空即可。

若要随包分发，请自行准备 ARM64 版本的 `ffmpeg` 与 `ffprobe`，
放进本目录并 `chmod +x`。
