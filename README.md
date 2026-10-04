# Beflow for Windows

<p align="center">
  <img src="src/BBDownForWindows.App/Assets/AppIcon.png" alt="Beflow logo" width="140">
</p>

**A Simple Desktop Video Downloader**

**基于 BBDown 构建的桌面视频下载器图形界面**

`Beflow for Windows` 使用 WinUI 3 与 .NET 10 构建，通过 [BBDown](https://github.com/nilaoda/BBDown) 完成 Bilibili 视频解析和下载。本项目是独立的第三方图形界面，不隶属于 BBDown 或哔哩哔哩。

![Beflow 主界面](docs/screenshot.png)

## 功能

- 先解析全部分集、逐集选流后下载，支持多分P、番剧及整季增量解析
- 个人主页投稿、合集与系列列表浏览，跨页勾选、全部分P选流并按视频独立批量入队
- 每集独立选择画质、真实分辨率、码率、编码与音频规格，支持批量规则和自动回退
- 杜比视界、HDR、4K 至 360P，AVC、HEVC 和 AV1
- E-AC-3、M4A、FLAC、AC-3、DTS 音频选择与自动回退
- WEB/TV 扫码登录及独立账号状态
- CDN、多线程、aria2c 自动调优、字幕、弹幕和封面
- 双链接或奇偶分P双音轨解析，支持双方独立选流、智能推荐主视频、逐集调整与 MKV 批量封装
- 下载后直接进入原生影视重命名，支持 TMDB、独立命名模板管理、媒体规格识别、字幕/弹幕/封面联动及安全撤销
- 批量下载进度、规格历史详情、失败集重试、持久日志和任务取消
- 普通下载与多音轨共用持久队列，支持等待任务编辑、排序和暂停续传
- 跟随系统、浅色与深色主题切换，并记忆上次选择
- 剪贴板链接自动识别与解析、下载及多音轨页面链接拖放，并提供独立开关
- 安装版在线更新

运行时不需要 Python、Node、Eel、Vue、WebView 或预先安装的 .NET Runtime。

## 下载与安装

从 [GitHub Releases](https://github.com/Townley9288/Beflow-for-Windows/releases) 下载 `Beflow-for-Windows-vX.Y.Z[.R]-win-x64-setup.exe` 安装版。项目不再发布便携版。

项目暂未购买代码签名证书，Windows SmartScreen 可能在首次运行时显示“未知发布者”。请只从本仓库 Release 下载，并可使用同名 `.sha256` 文件核对完整性。

安装版数据保存在 `%LOCALAPPDATA%\Beflow`，覆盖安装会保留现有配置和历史记录。

开启剪贴板监听后，在其他应用复制 B 站链接会自动填入并解析。当前位于多音轨封装页时，链接依次填入空的来源 A、B；解析期间复制的新链接会等待当前解析结束。两框都有链接时会询问替换哪个来源，重复链接不会覆盖另一来源。“同一链接奇偶分P”模式使用来源 A 输入框。

## 下载队列

队列页按“等待中”“进行中”“已完成”“失败 / 已取消”分栏显示任务和数量。尚未开始及正在编辑的任务放在“等待中”；已开始、正在停止或已暂停的任务放在“进行中”；部分失败也归入“失败 / 已取消”。

任务卡片在下载类型与成功／失败数量的同一行显示所选分集：单集展示实际分P编号和标题，多集展示连续范围及不连续编号（例如 `P1–P3、P5`），悬停可查看完整分集标题。较窄窗口下分集信息以省略号截断，成功／失败数量保留显示。多音轨分别标明来源 A、B 的分P编号。已有队列记录直接使用保存的分集信息，无需重新解析或下载。

解析并选择分集及音视频规格后，点击“加入队列”。普通下载和多音轨任务按加入顺序执行，下载时可以继续解析其他链接。尚未开始的任务可以编辑、上移或下移。

“暂停”保留当前任务的断点；“取消当前任务”结束该任务并继续下一个。关闭软件会保存队列，下次打开时有未完成任务才需手动点击“继续”；队列为空或只剩完成、失败、取消记录时，新加入的任务会自动开始。在本次运行中手动暂停后，仍需点击“继续”。内置单线程、多线程和 aria2 均支持断点续传，资源或断点校验不通过时会显示错误并保留文件。

内置传输完成并保存校验信息后，会清理该轨道的下载分片。多音轨的来源文件按“保留来源文件”设置处理；“清理完成记录”只移除队列记录，保留成品和历史记录。

## 个人主页批量下载

在侧栏打开“个人主页”，粘贴 `https://space.bilibili.com/UID` 并点击“读取主页”；也可以输入 UP 主名称，点击“搜索 UP 主”或按回车，再从候选列表中选择账号。候选项显示头像、名称、UID、粉丝数、认证和简介，支持手动加载更多、取消及失败重试；不会自动打开同名账号或搜索结果中的第一项。搜索复用本地 WEB 登录信息。普通下载页也会将主页链接转到此页面；拖入和剪贴板识别沿用设置中的对应开关。多音轨页面只接受具体视频来源。

1. 投稿目录完整读取后每页展示 30 条，可按分类、标题、发布时间筛选和排序。“合集和列表”先读取目录，打开一个条目才读取其中的视频。
2. 勾选视频，或使用“选择本页”“选择全部筛选结果”。翻页和切换投稿、合集会保留选择，同一 BV 只选一次。目录读取中断时保留已取得的结果，点击“继续读取”重试未完成的分页；全量选择在当前目录读取完成后可用。
3. 点击“解析所选视频”读取每个视频的全部分P。检查实际画质、分辨率、编码、音轨与替代原因，可逐项调整，也可应用现有批量规则。返回目录调整选择不会清除已解析规格；“继续解析／重试”仅处理未就绪的分P。
4. 选择下载根目录，点击“将就绪项加入队列”。界面会显示可入队和未就绪数量；每个视频成为一个独立任务，输出到 `所选目录/UP名（UID）/`，内部路径使用现有单视频或多分P命名模板。派生目录不会覆盖全局下载目录，已入队的视频会标记并锁定，避免重复提交。等待任务仍在下载页编辑。

主页目录使用本地 WEB 登录凭据；实际选流和下载沿用设置中的 API 模式。登录失效、风控或接口错误会明确提示，需用户手动重试。页面状态仅在本次运行期间保留。目录过长或文件冲突仍按现有规则报错，不覆盖已有文件，也不自动进行 TMDB 重命名。

开发验收步骤见 [主页批量下载验收工具](tests/SpaceIntegrationHarness/README.md)。

## 在线更新

软件默认在启动时每天最多检查一次 GitHub 稳定版，不会自动下载安装。关于页可以手动检查并确认更新：

- 安装版下载并校验新安装包，然后执行覆盖安装。

更新检查直接读取 GitHub Releases 的最新稳定标签，不调用 GitHub API；五分钟内重复检查使用本地内存缓存。该功能可以在设置页关闭，GitHub 暂时不可访问不会影响下载、登录和封装功能。

## 隐私与账号数据

Beflow 不上传配置、下载历史、重命名历史、任务日志或 B 站登录数据。`BBDown.data`、`BBDownTV.data` 和用户自行配置的 TMDB API Key 只保存在本地数据目录，并已从 Git 排除；TMDB Key 不会写入日志和历史。请不要在 Issue、日志截图或错误报告中公开这些文件的内容。

## 开发

要求 Windows 10/11 x64、Visual Studio 2022 和 .NET SDK 10.0.204。

```powershell
dotnet restore BBDown-for-Windows.sln --locked-mode
dotnet test BBDown-for-Windows.sln -c Release -p:Platform=x64
dotnet build src\BBDownForWindows.App\BBDownForWindows.App.csproj -c Release -p:Platform=x64
```

队列续传与完成后清理的本地 HTTP 验证：

```powershell
.\scripts\AcquireTools.ps1 -OutputDirectory artifacts/queue-validation/tools-build
dotnet build tests/QueueTransferHarness/QueueTransferHarness.csproj -c Release
python scripts/Test-QueueTransfers.py
```

每次运行会创建独立结果目录。真实 B 站队列与跨进程恢复验证见 `tests/QueueIntegrationHarness/README.md`。

aria2 使用默认 CDN 时会并行使用多个镜像，并按镜像数放大分片数，让每个节点都保持设置的连接数；手动指定 CDN 时只使用所选节点。完成工具构建后，可对本次生成的 BBDown 源码运行镜像选择回归检查（将路径替换为本次构建目录）：

```powershell
.\scripts\Test-BBDownAria2Mirrors.ps1 -WorkingDirectory tools/cache/work/bbdown-1.6.3-beflow.16-<构建进程ID>
```

生成发行包：

```powershell
.\scripts\Build-Release.ps1 -Version 1.1.1.16
```

BBDown 与 aria2 由脚本从官方 Release 下载。固定 FFmpeg 历史归档可通过 `-FfmpegArchiveUrl`、环境变量 `FFMPEG_ARCHIVE_URL` 或本地归档提供，所有工具下载均校验 SHA-256。

## 许可证

Beflow 源码使用 [MIT License](LICENSE)。发行包聚合的独立命令行工具保留各自许可证；详情见 [第三方许可](THIRD_PARTY_NOTICES.md) 与 [第三方源码](THIRD_PARTY_SOURCES.md)。
