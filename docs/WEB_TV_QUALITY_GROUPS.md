# WEB／TV 画质分组验收

验证日期：2026-10-17。用户确认在一个下拉框中分组，默认 WEB；TV 读取失败时保留可用 WEB 并单独提示。本功能随 v1.1.1.17 交付。

## 页面行为

- 普通下载、个人主页批量选流、多音轨来源共用同一套分组逻辑。上方 WEB 项保持原有名称、分辨率和码率，下方 TV 项显示“TV · 画质 · 分辨率 · 码率”。分组标题不可点击、不可通过键盘选中。
- 同名画质各自保存，例如 WEB 4K 和 TV 4K 不合并；WEB 返回的 SDR 增强继续显示在 WEB 组。
- 默认和批量规则使用 WEB，沿用原有端内规格匹配策略。手动选择 TV 项后，编码和音轨列表同步切换到 TV，来源写入选流快照，队列和多音轨来源固定该会话。TV 音轨不可用时明确提示并取消该行勾选，不使用 WEB 音轨补齐。
- TV 未配置时保持原来的 WEB 列表，不添加 TV 分组。TV 验证、HTTP、接口、结构或参数探测失败时显示独立警告，保留 WEB，需用户重新解析重试；WEB 验证或读取失败仍停止，不自动换到 TV。
- WEB 未配置时保留匿名 WEB 解析；已配置但失效或验证报错时不改为匿名。取消 TV 读取保留已经取得的 WEB 列表。
- 不同分P以及复用相同多音轨目录的多行分别保存当前来源，彼此不改变选择。逐步追加 TV 选项时保留用户已经确认的 WEB 手动规格。

## 自动验证

完整项目测试 509 项通过，覆盖 WEB 默认、双端列表、SDR 保留、同规格分组及唯一键、不可选择的标题、手动切换来源及音轨、批量规则不切换 TV、TV 失败保留 WEB、WEB 失败停止、取消、分P身份校验、只读取可用分P、来源验证缓存、共享目录的独立选择、历史选流恢复，以及普通队列、主页队列和多音轨的来源快照兼容。另有原有 TV 参数读取、媒体头探测和 CDN 参数回归，以及下述长篇多分P解析、风控停止、进度消息延迟和选择保留回归。

```powershell
dotnet test tests/BBDownForWindows.Tests/BBDownForWindows.Tests.csproj -c Release -r win-x64 --no-restore
dotnet publish src/BBDownForWindows.App/BBDownForWindows.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/tv-session-local/publish
```

原生 WinUI 组件运行检查位于 `artifacts/grouped-quality-ui`：用实际 `StreamQualityComboBox` 和选流 ViewModel 加载下述真实目录，在浅色、深色主题分别展开下拉框，检查分组标题的 `IsEnabled=false` 和 `IsTabStop=false`，并通过双向绑定切换 WEB→TV→WEB，确认音频选择及保存的会话同步变化。结果保存在 `artifacts/grouped-sessions-av625160382-20261017/ui-proof.json`。测试窗口自行关闭，不运行正式版 AppServices，不访问正式队列或历史。

## 真实账号与同集验证

使用 `https://www.bilibili.com/video/av625160382` 的 P1《大理寺日志》“1 洛阳寻兄”。仅将本地 WEB、TV 凭据复制到独立验收 Runtime，结束删除副本并核对两份原文件 SHA-256 未变。验收目录为 `artifacts/grouped-sessions-av625160382-20261017`，此次没有新增媒体下载。

- WEB 实际返回 8 个画质选项，其中包含 `4K·SDR增强 · 3840×2160 · 9794 kbps`；TV 返回 7 个画质选项，均有 `TV · ` 标记。两个分组中的 4K AVC 都为 `5608 kbps`，分别保留。
- 默认选流快照：`ApiMode=WEB`，4K AVC、3840×2160，M4A 191 kbps。
- 手动选择 TV 4K 后的快照：`ApiMode=TV`，4K AVC、3840×2160，M4A 323 kbps。无规格替代原因，WEB 音频未被带入 TV。
- 原生控件在浅色和深色主题中切回 WEB 后，音频选择恢复为 WEB 的 191 kbps；分组标题保持不可选。
- `catalog.json` 保存两端独立规格，`grouped-selection.json` 保存实际下拉选项及两次选流快照，`parse-commands.json` 记录 WEB 和 TV 各一次原生解析。凭据及签名媒体地址不写入验收结果。

此前同一视频的 TV 成品、真实队列执行及整集音视频解码结果见 [TV 会话验收记录](TV_SESSION_ACCEPTANCE.md)。

## 长篇多分P解析修正与验证

用户报告 `BV1BhuezaEgB` 的完整搜索链接无法解析。正式版日志显示：目录阶段在未指定 `-p` 的情况下连续请求全部 123 个分P，于 P115 收到 HTTP 412；再次解析时目录请求及其后并发请求均返回 412。BBDown 的 `--show-all` 只控制目录显示，并不限制解析范围，未指定分P且链接没有 `p` 参数时实际会处理全部分P。

修正后的全部解析在目录阶段使用 `-p 1 --show-all`，取得完整目录并保留 P1 的规格，再按既有并发设置仅解析剩余分P。当前集解析对 BV／av 视频明确指定 URL 中的 `p`，未指定时只读取 P1；番剧链接及番剧跳转继续保留 BBDown 根据 EP 身份选择当前集的行为。本次不新增接口或会话回退，不改变全局并发设置。

2026-10-17 使用用户提供的完整链接（含 `spm_id_from` 和 `vd_source`）及正式版登录凭据的隔离副本，经过实际 `BBDownService`、原生 BBDown 和双端画质组合流程验证：

- 当前集：完整目录 123 项，仅解析 P1；WEB 和 TV 均可用，约 2.23 秒。
- 全部集：123 项均就绪，123 项均取得独立 TV 规格，无 TV 警告，约 8.94 秒。沿用用户的 8 路并发；WEB 目录请求仅包含 P1，其余 WEB 及 TV 请求按互不重复的分P组提交，未出现 412。
- 新增测试覆盖 123 分P的目录读取范围、各分P只请求一次、完整目录保留，以及当前集的搜索跟踪参数、`p=3`、av 链接和纯 BV 输入。测试用进程同时修正为模拟 BBDown 在无 `-p` 时实际解析全部分P的行为。
- 验收结果和命令范围保存在 `artifacts/multipart-parse-acceptance/current/result.json` 和 `artifacts/multipart-parse-acceptance/all/result.json`，隔离日志位于各自的 `local/Beflow/Logs`。结束删除凭据副本，并校验正式版两份凭据的 SHA-256 未变；未修改正式版队列、历史及设置，也未新增下载任务。

实际耗时及接口可用性是本次验证结果，不作为服务端持续可用的保证。

## 解析不完整与选流绑定修正

18:27 的两次正式数据目录日志确认：修正目录范围后，WEB 的 P1 即返回 HTTP 412，只有 P7 返回视频流。界面的“已解析 123 集”实际统计了包括失败在内的处理记录；完成后尚在派发队列里的进度消息还会重新显示运行中的进度条。本地独立进程随后也复现了 HTTP 412，前一次成功不能证明接口持续可用。

这次增加以下处理：

- 在信息解析进程首次返回 HTTP 412 或接口错误 -352 时终止该解析进程，阻止 BBDown 的自动重试。目录阶段遇到风控不再启动后续分P工作；并发阶段取消本轮其他工作，保留完整目录及已成功的分P，剩余分P保持待解析。不切换接口或登录会话，不修改用户的并发、代理或 CDN 设置。
- 显示实际“就绪／目录总数”，并列出失败和待解析数量；不完整时保留警告和日志入口。结束后忽略尚未处理的进度消息，用户仍可手动继续解析。
- 独立的 WinUI 控件验收复现了截图中的 P7 问题：WEB 行先选好 AVC，TV 规格随后加入时清空并重建 ComboBox 项，会通过双向绑定清空编码选择，导致没有可入队的视频且大小只剩音频。现在增量插入 TV 分组和选项，保留现有 WEB Choice 对象与质量、编码、音频及手动选择，避免集合重置。

真实 WinUI 验收程序位于 `artifacts/multipart-ui-acceptance`，使用 `DownloadViewModel`、`AppServices`、真实 Dispatcher 和双向绑定的画质／编码／音频控件；应用数据目录独立，未初始化或运行正式队列。验收结果：

- `partial/ui-proof.json` 用此前真实规格重放 WEB 先到、TV 后到及 HTTP 412 部分失败：显示“就绪 1/123 集 · 失败 1 集 · 待解析 121 集”，进度关闭，P7 保持 AVC，视频和音频均有选择，可加入队列，可继续解析。
- `complete/ui-proof.json` 用此前真实 123 集目录重放同样的逐步画质更新：123 集全部就绪，编码保持 AVC，可入队，无待解析分P，进度关闭。
- `live/ui-proof.json` 使用用户完整搜索链接和正式登录凭据的隔离副本，经过真实 `AppServices → BBDownService → 原生 BBDown → DownloadViewModel → WinUI 双向绑定` 流程：123 集 WEB 和独立 TV 规格均可用，P1 保持 AVC，成品估算约 1.26 GiB，视频和音频都有选择，可入队，无错误，进度正常关闭。验收没有执行下载，结束后删除凭据副本并检查正式凭据未变。

诊断过程中，同一 WEB 接口的原生 BBDown 调用曾返回 412、也曾成功，直接请求也成功；不能据此将间歇性错误归因于 UA、代理或登录失效。本次不增加请求重试、换端、匿名或其他回退。出现 412 时用户需要等待后手动重试，不能承诺一次解析持续成功。
