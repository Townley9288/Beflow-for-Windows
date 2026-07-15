# Beflow for Windows v1.0.1

这是一次登录与发布包修复更新。

## 修复

- 修复设置页显示 WEB/TV 账号已登录，但 BBDown 下载和信息解析仍提示“尚未登录”的问题。
- BBDown 现在从用户私有 Runtime 目录运行，与 `BBDown.data`、`BBDownTV.data` 保持在同一目录。
- 修复部分需要账号权限的视频因 BBDown 未加载 Cookie 而返回 `code=-404`、无法获取视频流的问题。
- BBDown 随软件升级发生变化时，Runtime 中的托管副本会自动安全刷新。

## 安装包

- WinUI 语言资源只保留简体中文、繁体中文和英语，减少无用语言目录。
- 从 v1.0.0 覆盖安装时，安装程序会自动清理旧版本遗留的其他 WinUI MUI 语言目录。
- 安装版和便携版均继续保留原有配置、历史、日志及登录数据。

发行文件暂未进行商业代码签名，Windows SmartScreen 可能显示未知发布者。请从本 Release 下载，并使用对应 `.sha256` 文件核验完整性。
