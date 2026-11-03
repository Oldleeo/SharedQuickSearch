#!/usr/bin/env bash
# 编出共享速搜自带的精简版 ffmpeg.exe（Windows x64，LGPL 2.1+，静态链接，不依赖任何 DLL）。
# 只编进“从视频里取一帧”要用的东西：常见视频格式的解码器、缩放、自动转正、输出 BMP。
#
# 用法（Ubuntu / Debian / WSL）：
#   sudo apt-get install -y git make nasm mingw-w64
#   ./build-ffmpeg.sh            # 输出 ffmpeg.exe.gz 和 ffmpeg.exe.sha256 到当前目录
#
# 源码：FFmpeg 官方仓库 tag n7.1（https://github.com/FFmpeg/FFmpeg），没有任何修改。
set -euo pipefail
VERSION="n7.1"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${WORK:-$(mktemp -d)}"
echo "工作目录：$WORK"
git clone --depth 1 --branch "$VERSION" https://github.com/FFmpeg/FFmpeg.git "$WORK/src"
mkdir -p "$WORK/build" && cd "$WORK/build"
"$WORK/src/configure" \
  --enable-cross-compile --target-os=mingw32 --arch=x86_64 --cross-prefix=x86_64-w64-mingw32- \
  --enable-w32threads --disable-pthreads --extra-ldflags="-static -static-libgcc" \
  --disable-everything --disable-autodetect --disable-doc --disable-debug --disable-network \
  --disable-programs --enable-ffmpeg --disable-avdevice --disable-postproc \
  --enable-protocol=file,pipe \
  --enable-demuxer=mov,matroska,avi,asf,mpegts,flv,mpegps \
  --enable-decoder=h264,hevc,mpeg4,mpeg2video,mpeg1video,mjpeg,prores,vp8,vp9,wmv3,vc1,msmpeg4v3,dnxhd,h263 \
  --enable-parser=h264,hevc,mpeg4video,mpegvideo,vp8,vp9,mjpeg,vc1,h263 \
  --enable-filter=scale,format,null,buffer,buffersink,transpose,hflip,vflip \
  --enable-encoder=bmp --enable-muxer=image2pipe --enable-swscale
make -j"$(nproc)"
x86_64-w64-mingw32-strip -o ffmpeg-stripped.exe ffmpeg.exe
# 确认没有依赖 mingw 的 DLL（只能有 Windows 自带的）
if x86_64-w64-mingw32-objdump -p ffmpeg-stripped.exe | grep -i "DLL Name" | grep -viE "kernel32|msvcrt|user32|advapi32|bcrypt|shell32|ole32|api-ms-win|ucrtbase|ws2_32|psapi|secur32|gdi32"; then
  echo "依赖了不该依赖的 DLL" >&2; exit 1
fi
gzip -9 -n -c ffmpeg-stripped.exe > "$HERE/ffmpeg.exe.gz"
sha256sum ffmpeg-stripped.exe | cut -d' ' -f1 > "$HERE/ffmpeg.exe.sha256"
echo "完成：$HERE/ffmpeg.exe.gz（$(stat -c %s "$HERE/ffmpeg.exe.gz") 字节）"
