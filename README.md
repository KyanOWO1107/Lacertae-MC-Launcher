# Lacertae Minecraft Launcher

Lacertae 是一个 Windows 10/11 优先、保留跨平台服务抽象的 Minecraft 启动器。当前仓库处于 M1 开发阶段，面向普通玩家和小范围测试用户；默认交互保持简单，高级 Java/JVM、版本隔离和诊断能力按需展开。

> 当前不是正式发行版。README 只描述已经在仓库中实现或经过检查的能力；未完成能力会明确标为后续里程碑。

## 项目目标

- Windows 10/11 x64 优先，同时把路径、认证、下载和平台能力放在可替换边界内，为后续 macOS/Linux 保留空间；
- 采用便携版发行方式，不依赖安装器或系统集成；
- 对普通玩家保持简单默认设置，对 Java、JVM、版本隔离、诊断和更新提供渐进式高级选项；
- 核心代码采用 Apache-2.0，官方品牌、Logo、签名密钥和官方更新渠道单独管理。

## 当前 M1 已实现

- 原版版本目录浏览、安装/修复任务和启动前预检；
- 离线账号模型、多账号持久化边界和安全日志；
- Java 发现、版本兼容选择、内存/GC/JVM 参数解析；
- 五种版本隔离策略的领域模型与本地资源目录入口；
- 可预览、脱敏、限额的诊断包；
- 签名更新清单校验、包暂存、独立 Updater 替换/健康确认/回滚骨架；
- Microsoft 认证适配器边界：显式公共客户端、固定 loopback 回调、按账号的短期 MSAL 缓存桥接、Xbox/XSTS 和 Java Edition 所有权检查链；
- Microsoft 账号添加、静默刷新、重新认证状态和默认/单版本账号解析编排，并已接入桌面多账号页；
- Windows DPAPI 当前用户密钥库：32 字节随机 entropy、版本化 `LCSV` 密文格式、原子替换、当前用户 ACL 和稳定错误映射；
- Minecraft 头像缓存：仅接受 `textures.minecraft.net` 的 HTTPS 纹理地址，限制 1 MiB、严格校验非动画 PNG，并按 SHA-256 内容寻址原子缓存；失败时返回本地占位状态；
- 账号删除恢复：先标记 `Deleting`、清理 Microsoft 密钥、清除版本引用和默认账号，并在启动阶段重试中断删除；离线账号不会触碰密钥库；桌面页要求玩家名确认并在删除中禁用账号行；
- 桌面账号页：离线/Microsoft 多账号列表、正版/离线与重新认证状态、默认账号和当前版本账号覆盖、启动账号摘要、可取消浏览器登录、受保护删除和本地头像/占位展示；
- Avalonia 桌面壳、主题/可访问性基础和非阻塞更新提示；
- Windows x64 便携版构建、包内文件清单、SHA-256 校验、SBOM 和第三方声明。

以下能力仍属于后续里程碑：Forge/Fabric/NeoForge、Mod/材质包在线资源、整合包、第三方统一通行证、主页模块导入、完整 Microsoft 登录生产验收，以及 macOS/Linux 发行。Microsoft 真实登录仍需维护者提供自有 Entra Public Client 注册后进行实机验收。

## 开发环境与验证

需要 .NET SDK `10.0.302`（版本由 [`global.json`](global.json) 约束）；Windows 开发推荐 PowerShell 7。运行完整检查：

```powershell
./eng/verify.ps1
```

该脚本执行锁定还原、Release 构建、全量测试、格式检查和 Git diff 检查。生成 Windows x64 便携候选包：

```powershell
$env:SOURCE_DATE_EPOCH = '1700000000'
./eng/publish.ps1 -Runtime win-x64 -Version 0.1.0-test -OutputDirectory artifacts/release-candidate
./eng/verify-package.ps1 -PackageDirectory artifacts/release-candidate/package
```

发布候选包必须包含根目录 [`LICENSE`](LICENSE)，并随包携带许可证、`THIRD-PARTY-NOTICES.txt`、`sbom.cdx.json` 和 `package-manifest.json`。签名发布前仍需完成真实更新公钥、`keyId`、GitHub 仓库地址和离线签名流程配置。

维护者的 Entra 审核资料放在被 Git 忽略的 `.entra-id` 中；它不是运行时配置。需要进行本地 Microsoft 登录验收时，使用 [`prepare-oauth-local.ps1`](eng/prepare-oauth-local.ps1) 将其中的公共 Client ID 转换为可执行文件目录旁的 `oauth.local.json`。该文件不能进入 Git 或发布 ZIP。

## 架构

仓库采用分层 .NET/Avalonia 结构：

```text
Domain → Application → Infrastructure
                     ↘ Platform.Windows
                           ↘ Desktop

Updater（独立进程，仅依赖 Domain）
```

详细边界见 [`docs/decisions/0001-modular-monolith.md`](docs/decisions/0001-modular-monolith.md)。

## 数据与便携模式

默认配置和数据库位于 `%APPDATA%\Lacertae`，日志、缓存、托管 Java 和更新暂存位于 `%LOCALAPPDATA%\Lacertae`。在启动器目录手动创建 `lacertae.portable` 后才使用当前目录数据；便携 ZIP 不携带该标记或用户数据，因此移动启动器位置不会改变默认数据位置。

## 更新分发

更新清单使用 ECDSA P-256/SHA-256 detached signature，支持 `stable`、`preview`、`test`、`nightly` 四个固定渠道，`stable` 默认启用。初期使用 GitHub Releases 静态资产承载 `manifest.json`、`manifest.sig` 和 ZIP，不要求自建更新应用服务器；后续可以增加自有域名或 CDN 作为稳定入口。

启动器主页可以单独部署到维护者的云服务器和域名，用于文档、公告与项目介绍；主页服务器不承担更新 ZIP 分发，更新资产仍通过 GitHub Releases 承载。

客户端只接受预配置的 HTTPS 地址，并独立校验清单签名、包大小、ZIP SHA-256 和包内文件清单。真实 `keyId`、公钥和发布仓库配置完成前，自动更新保持关闭。完整约束见 [`docs/decisions/0002-update-distribution.md`](docs/decisions/0002-update-distribution.md) 和 [`docs/releasing/windows-portable.md`](docs/releasing/windows-portable.md)。

## 文档

- [`CHANGELOG.md`](CHANGELOG.md)：面向用户和维护者的变更记录；
- [`CONTRIBUTING.md`](CONTRIBUTING.md)：贡献、验证和许可证边界；
- [`docs/README.md`](docs/README.md)：文档索引与维护规则；
- [`docs/testing/m1-windows-acceptance.md`](docs/testing/m1-windows-acceptance.md)：Windows 10/11 验收矩阵；
- [`docs/testing/microsoft-login.md`](docs/testing/microsoft-login.md)：Microsoft 登录配置门控和验收边界；
- [`docs/third-party-dependencies.md`](docs/third-party-dependencies.md)：依赖、版本和许可证来源；
- [`docs/third-party-notices.md`](docs/third-party-notices.md)：发布包第三方声明与复核要求。

## 许可证

核心源代码按 [`Apache License 2.0`](LICENSE) 授权。该许可证不授予 Lacertae 官方名称、Logo、主题资源、签名密钥或官方更新渠道的使用权；第三方依赖仍以各自许可证和 [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) 为准。
