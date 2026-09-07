# 队列集成验收

使用实际 Core 队列服务、BBDown、FFmpeg、aria2 和 mkvmerge。所有任务和下载结果写入指定的独立目录。工具目录不能包含 `portable.flag`。

先准备工具，再用三个独立进程分别执行入队、暂停退出和继续完成：

```powershell
.\scripts\AcquireTools.ps1 -OutputDirectory artifacts/queue-validation/tools-build
dotnet build tests/QueueIntegrationHarness/QueueIntegrationHarness.csproj -c Release

$queueHarness = 'tests/QueueIntegrationHarness/bin/Release/net10.0/QueueIntegrationHarness.dll'
$queueTools = 'artifacts/queue-validation/tools-build'
$queueRun = 'artifacts/queue-validation/live-' + (Get-Date -Format yyyyMMdd-HHmmss)
$queueSourceA = 'https://www.bilibili.com/video/av170001'
$queueSourceB = $queueSourceA
$queueMkvmerge = 'D:\Software\MKVToolNix\mkvmerge.exe'

dotnet $queueHarness prepare $queueTools $queueRun $queueSourceA $queueMkvmerge $queueSourceB
dotnet $queueHarness pause $queueTools $queueRun $queueSourceA $queueMkvmerge $queueSourceB
dotnet $queueHarness complete $queueTools $queueRun $queueSourceA $queueMkvmerge $queueSourceB
```

需要登录时，在每条命令最后追加现有 `BBDown.data` 文件路径。验收程序会复制到独立 Runtime，结束时删除副本并核对原文件哈希。不要把账号数据写入命令文本或结果报告。

程序选择第一集的最低分辨率/码率，依次验证内置单线程、多线程、aria2，以及保留来源和删除来源两种多音轨任务。两路来源相同可检查双音轨文件结构；真实双语内容或音轨同步需传入对应的两个版本链接另行验收。

`prepare-results.json` 记录选流，`pause-results.json` 记录退出前的真实分片，`complete-results.json` 记录输出 SHA-256、音视频轨道数、分片清理和历史去重结果。准备阶段要求空队列；超时或失败会返回非零退出码，并保留下载现场。
