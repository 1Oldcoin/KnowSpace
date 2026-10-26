# ffmpeg / linux-x64

只有处理**视频**资料才需要它；纯文本 / PDF / DOCX 知识库可以完全不放。

推荐做法（二选一）：

1. **用系统包管理器**（容器里就这么做）：
   ```bash
   apt-get install -y --no-install-recommends ffmpeg
   ```
   程序会自动回落到 PATH 里的 `ffmpeg`，本目录保持为空即可。

2. **随包分发**一个静态编译版：
   ```bash
   curl -fL -o ffmpeg.tar.xz https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz
   tar -xf ffmpeg.tar.xz
   mv ffmpeg-*-static/ffmpeg  ./ffmpeg
   mv ffmpeg-*-static/ffprobe ./ffprobe
   chmod +x ffmpeg ffprobe
   ```

放好后 `ExternalTools/ffmpeg/linux-x64/ffmpeg` 会被自动识别。
