# Security Policy

## Supported versions

安全修复优先提供给最新稳定版。发现问题后请先升级到最新 GitHub Release 再复现。

## Reporting a vulnerability

请使用 GitHub 仓库的 **Security → Report a vulnerability** 私下报告安全问题。不要为可能泄露账号数据、文件路径或任意代码执行的问题创建公开 Issue。

报告请包含 Beflow 版本、Windows 版本、复现步骤和已脱敏日志。请勿提交以下内容：

- `BBDown.data`、`BBDownTV.data` 或二维码文件
- Cookie、SESSDATA、access token、refresh token
- TMDB API Key 或包含账号信息的代理地址
- 包含个人目录、下载历史或账号资料的完整配置和日志

## Local data and updates

Beflow 的设置、下载历史、重命名历史、日志、TMDB API Key 和 B 站登录凭据保存在本地，不会由应用上传。TMDB Key 仅用于用户主动发起的 TMDB 请求，不写入任务日志或历史。在线更新只访问本仓库的 GitHub Releases，下载完成后必须通过同一 Release 提供的 SHA-256 校验才会执行。

当前发行版未进行商业代码签名，SHA-256 可以检测下载损坏，但不能替代发布者代码签名。请只从 <https://github.com/Townley9288/Beflow-for-Windows/releases> 获取安装包和便携包。
