# 个人主页实际接口与下载验收

运行前准备包含 BBDown、aria2c、ffmpeg/ffprobe 的应用工具目录，以及已登录的本地 `BBDown.data`。工具目录不能包含 `portable.flag`。测试目录必须不存在；建议使用短路径，避免实际长视频标题与默认命名模板组合超过现有路径长度限制。

```powershell
dotnet run --project tests/SpaceIntegrationHarness -c Release -- `
  D:\BeflowTools D:\BeflowSpaceTest\run-001 "$env:LOCALAPPDATA\Beflow\Runtime\BBDown.data"
```

最后追加 `list-only` 可只验证目录接口。完整运行会：

- 顺序读取 UID `538596213` 的全部投稿、合集目录及各合集成员，并记录接口总数和去重后数量；不写死当前视频数量。
- 读取 UID `23630128` 的系列 `340933`，补充主验收账号没有系列的场景。
- 从投稿中选择两条 6–45 秒的视频，以全部分P模式解析、360P / AVC 规则独立批量入队。
- 使用仅属于验收进程的 aria2 包装器限速，在产生 `.aria2` 断点文件后暂停并继续；生产下载参数不受影响。
- 检查两项完成、输出位于正确的 UP 目录、文件校验信息一致，且 ffprobe 返回一条视频和一条音轨。

所有配置、队列、历史、成品和 `results.json` 写入独立测试目录。测试只复制 WEB 凭据，结束时删除副本并验证原凭据的 SHA-256 未变化；不会读取或修改用户已有队列、历史。异常运行也会在 `finally` 中清理凭据并写出已完成的验收记录，只有打印 `SPACE_ACCEPTANCE_PASSED` 才表示完整通过。

自动化测试与构建：

```powershell
dotnet test BBDown-for-Windows.sln -c Release -p:Platform=x64
dotnet build src/BBDownForWindows.App -c Release -p:Platform=x64
python scripts/Test-QueueTransfers.py
```

界面验收使用独立的便携测试副本：检查浅色／深色、窗口缩放、跨页勾选、标题搜索、合集成员、返回后手动规格保留、批量入队，以及队列下载期间浏览和解析主页。便携副本仅为隔离验收数据，不生成发行包。
