# BBDown for Windows

`BBDown for Windows` 是基于 WinUI 3 与 .NET 10 构建的原生 Bilibili 下载器界面，调用 [BBDown](https://github.com/nilaoda/BBDown) 完成解析和下载。

## 功能

- 普通视频、多分P、番剧及整季下载
- 杜比视界、HDR、4K 至 360P，AVC/HEVC/AV1
- 指定 E-AC-3、M4A、FLAC、AC-3、DTS 音频格式并自动回退
- WEB/TV 扫码登录
- CDN、多线程、aria2c 与可读参数配置
- 逐集分辨率优先的整季 4K 下载
- 双链接或奇偶分P的双音轨下载、延迟和 MKV 批量封装
- 已有任务目录仅重新封装
- 下载历史、增量日志、持久日志和任务取消

运行时不需要 Python、Node、Eel、Vue 或浏览器页面。

## 开发

要求 Windows 10/11 x64、Visual Studio 2022 或 .NET SDK 10.0.204。

```powershell
dotnet restore BBDown-for-Windows.sln --locked-mode
dotnet test BBDown-for-Windows.sln -c Release -p:Platform=x64
dotnet build src\BBDownForWindows.App\BBDownForWindows.App.csproj -c Release -p:Platform=x64
```

调试构建会自动从旧版目录或 PATH 查找 BBDown、aria2c、FFmpeg 和 MKVToolNix。真实登录数据、配置、历史和日志不会进入 Git。

## 发布

```powershell
.\scripts\Build-Release.ps1 -Version 1.0.0
```

脚本生成自包含便携 ZIP、SHA-256 和可选 Inno Setup 安装包。BBDown 与 aria2 由脚本从官方 GitHub Release 下载。由于固定的 2024-01-10 FFmpeg 历史归档已从滚动发布索引中移除，本地构建会自动在仓库上级目录和各固定磁盘的 `Software` 目录查找 `ffmpeg-N-113240-g6d2f64534d-win64-gpl.zip`，也可以通过 `-FfmpegArchivePath` 显式指定。

CI 发布前需要把同一归档上传到稳定地址，并设置仓库变量 `FFMPEG_ARCHIVE_URL`；归档 SHA-256 必须为 `D69FA64F6EEDFF7EC247D6C7870F8041F1A7E2254165FA1429F40A077FC54955`。

## 用户数据

- 安装版：`%LOCALAPPDATA%\BBDownForWindows`
- 便携版：程序旁的 `Data` 目录

首次启动会无损复制旧 Windows 版的 `config.json`、`BBDown.data` 和 `BBDownTV.data`，不会删除原文件。
