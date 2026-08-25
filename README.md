# 种马搜索器安装助手（私有测试）

这是“非扩展商店安装方案”的独立测试仓库。它提供一个 Windows 图形安装助手，帮助普通用户安装和更新[种马搜索器](https://github.com/yyahz/umamusume-seed-optimizer)浏览器扩展。

本仓库只包含安装助手，不复制扩展源代码。当前版本用于验证流程，不建议公开传播。

## 它解决什么问题

- 自动查找公开 GitHub Release 中的最新扩展包；
- 显示安装助手自身版本，并支持绕过网络缓存强制刷新；
- 下载后核对 GitHub 提供的 SHA256 摘要；
- 把扩展安装到固定目录，更新时保持路径不变；
- 更新前自动备份旧版本，失败时恢复；
- 检测 Chrome、Edge、360 安全浏览器和 360 极速浏览器；
- 一键复制扩展目录和浏览器扩展管理页地址，不产生空白标签页。

受 Chromium 安全限制，第一次安装仍需用户在扩展管理页手动完成一次确认：开启“开发者模式”，点击“加载已解压的扩展程序”，选择助手复制的目录。后续更新只需运行助手并在扩展管理页点击“重新加载”。助手不会绕过浏览器的安全确认。

## 测试者使用方法

> 推荐下载文件名包含 `win-compact` 的轻量版。它依赖 Windows 10/11 自带的 .NET Framework 4.8，体积约几百 KB。只有轻量版无法启动时，才下载约70 MB的 `win-x64-full` 完整版。

| 文件 | 推荐场景 | 运行要求 |
| --- | --- | --- |
| `UmaSeedInstaller-v*-win-compact.exe` | 默认推荐 | Windows 10 1903及以上或Windows 11 |
| `UmaSeedInstaller-v*-win-x64-full.exe` | Compact 无法启动时备用 | 64位Windows，自带全部运行库 |

1. 从本仓库的 Releases 下载最新版 `win-compact.exe`。
2. 双击运行，无需管理员权限。
3. 点击“安装 / 更新到最新版”。
4. 点击“复制管理页地址”，到所选浏览器地址栏粘贴并回车；然后返回助手点击“复制扩展目录”。
5. 首次安装时开启开发者模式并加载该目录；以后更新时点击扩展卡片上的“重新加载”。360 安全浏览器如果没有“重新加载”按钮，可先停用扩展再启用。

如果以前从其他文件夹加载过扩展，需要先删除旧的扩展项，再从助手提供的固定目录加载一次。完成这次迁移后，后续更新不再更换目录。

Windows 可能对未签名的测试程序显示 SmartScreen 提示。这是因为测试版尚未购买代码签名证书，并不代表程序请求了管理员权限。请只从本仓库 Release 下载，并核对同一 Release 中的 SHA256 文件。

## 安装位置与备份

| 内容 | 位置 |
| --- | --- |
| 当前扩展 | `%LOCALAPPDATA%\UmaSeedSearcher\Extension` |
| 旧版本备份 | `%LOCALAPPDATA%\UmaSeedSearcher\Backups` |
| 临时工作文件 | `%LOCALAPPDATA%\UmaSeedSearcher\Work` |

早期测试版使用过包含作者名的旧目录。新版检测到旧目录时会先征求用户同意，再将整个目录迁移到上述位置；不会覆盖已经存在的新目录。

助手不修改注册表，不写入浏览器安装目录，不关闭浏览器，也不访问浏览器用户资料目录。

## 隐私与安全边界

完整说明见 [PRIVACY.md](PRIVACY.md) 和 [SECURITY.md](SECURITY.md)。核心原则是：

- 不读取或保存 Cookie、密码、浏览历史、书签及浏览器个人资料；
- 不收集设备标识、用户名、邮箱、遥测或使用统计；
- 仅访问 GitHub API 和 GitHub Release 下载地址；
- 仅接受固定公开仓库、固定文件名、Manifest V3 且名称/版本匹配的扩展包；
- 校验文件大小、SHA256、ZIP 路径和允许的文件类型后才安装。

## 本地构建

需要 Windows、.NET 8 SDK 或更高版本，以及本机的 .NET Framework 4.8 目标包：

```powershell
pwsh -File .\scripts\build-release.ps1
```

输出位于 `artifacts`：

- 基于 .NET Framework 4.8 的单 EXE Compact 版；
- 自包含的 Windows x64 Full 版；
- 两个版本各自的 `.sha256.txt` 校验文件。

运行自动测试：

```powershell
dotnet run --project .\tests\UmaSeedInstaller.Tests\UmaSeedInstaller.Tests.csproj -c Release
dotnet run --project .\tests\UmaSeedInstaller.Compact.Tests\UmaSeedInstaller.Compact.Tests.csproj -c Release
```

连同公开 GitHub Release 的真实下载、校验和临时安装一起测试：

```powershell
dotnet run --project .\tests\UmaSeedInstaller.Tests\UmaSeedInstaller.Tests.csproj -c Release -- --integration
dotnet run --project .\tests\UmaSeedInstaller.Compact.Tests\UmaSeedInstaller.Compact.Tests.csproj -c Release -- --integration
```

## 发布测试版

推送 `v*` 标签后，GitHub Actions 会在 Windows runner 上分别测试并构建 Compact 与 Full 两种程序，然后创建预发布版本。工作流不会保存用户数据，也不需要第三方密钥。

## 许可证

[MIT License](LICENSE)
