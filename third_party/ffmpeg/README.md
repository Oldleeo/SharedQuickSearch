# 自带的精简版 ffmpeg

共享速搜用它从视频里取一帧做缩略图。只用于取帧，不做别的。

| | |
|---|---|
| 文件 | `ffmpeg.exe.gz`（gzip 压缩的 Windows x64 程序，编译时嵌进共享速搜，第一次用到视频时解到 `%LocalAppData%\Oldlee\共享速搜\tools`） |
| 版本 | FFmpeg `n7.1`，官方源码，没有任何修改：https://github.com/FFmpeg/FFmpeg/tree/n7.1 |
| 许可 | LGPL 2.1 或更新版本（全文见 `COPYING.LGPLv2.1`）。没有启用任何 GPL 或 nonfree 组件 |
| 怎么编的 | `build-ffmpeg.sh`（Ubuntu/WSL + mingw-w64 交叉编译，静态链接，不依赖 DLL） |
| 校验 | `ffmpeg.exe.sha256` 是解压后 exe 的 SHA-256 |

只编进了这些：

- 视频格式：MP4 / MOV、MKV / WebM、AVI、WMV / ASF、MPEG-TS、FLV、MPEG-PS
- 解码器：H.264、H.265 / HEVC、MPEG-4、MPEG-1/2、MJPEG、ProRes、VP8、VP9、WMV9 / VC-1、DNxHD、H.263
- 处理：缩放、按旋转信息转正（transpose / hflip / vflip）、输出 BMP

没有编进去的格式（比如 AV1）照旧交给 Windows 生成缩略图。

想自己重编一份核对：

```bash
sudo apt-get install -y git make nasm mingw-w64
./build-ffmpeg.sh
```
