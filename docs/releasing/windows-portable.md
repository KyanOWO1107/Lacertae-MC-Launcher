# Windows 便携版发布

当前发布目标是 Windows 10/11 x64 便携 ZIP。启动器默认将配置/数据库写入 `%APPDATA%\Lacertae`，日志、缓存、运行时和更新暂存写入 `%LOCALAPPDATA%\Lacertae`。用户在程序目录创建 `lacertae.portable` 后才启用当前目录数据模式；发布 ZIP 不包含该标记，也不包含 `LacertaeData`。

## 本地候选包

在 Windows PowerShell 中运行：

```powershell
./eng/publish.ps1 -Runtime win-x64 -OutputDirectory artifacts/release-candidate
./eng/verify-package.ps1 -PackageDirectory artifacts/release-candidate/package
```

脚本固定使用 `Release`、`win-x64`、self-contained 和非单文件输出，Updater 放在 `Updater/` 子目录。包内 `package-manifest.json` 按相对路径排序并记录大小/SHA-256；`sbom.cdx.json` 和第三方声明随包发布。

`verify-package.ps1` 在缺少所有者批准的根目录 `LICENSE` 时故意失败。此门槛不能通过环境变量、测试参数或自动生成的许可证绕过。

## 更新资产

构建和打包阶段只生成未签名 ZIP 与 manifest。离线签名流程另行执行：使用受控环境中的 ECDSA P-256 私钥签署 canonical manifest，随后将 `manifest.json`、`manifest.sig` 和 ZIP 发布到 GitHub Releases 或静态 HTTPS/CDN。私钥不进入仓库、CI、ZIP 或客户端；客户端只内置经批准的 SPKI DER 公钥。

`stable` 默认渠道；`preview`、`test`、`nightly` 只在构建配置显式启用时发布。真实域名、`keyId` 和公钥未配置前，自动更新保持禁用。
