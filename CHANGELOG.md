# Changelog

本文件记录 Lacertae 的用户可见变化、发布边界和重要维护变化。项目当前处于 M1 开发阶段，尚未发布正式版本；`Unreleased` 中的内容不代表已经生成公开发行包。

## [Unreleased]

### Added

- 根目录 Apache License 2.0，覆盖核心源代码、Updater、更新协议、构建脚本和文档；
- 项目 README、贡献指南和文档索引；
- GitHub Releases 静态更新分发说明，包含 `stable`、`preview`、`test`、`nightly` 渠道边界；
- Microsoft 公共客户端配置门控、严格的 `oauth.local.json` 校验和启动状态提示；
- Microsoft 认证适配器边界：显式 MSAL 公共客户端、固定 `http://localhost` 回调、按账号缓存桥接、CmlLib Xbox/XSTS/Java Edition 所有权链和稳定错误映射；
- Microsoft 账号添加、静默刷新、重新认证状态和默认/单版本账号选择的 Application 编排；
- SQLite 账号资料 v2 追加迁移：校验稳定账号 ID、账号类型和状态，并建立状态索引；迁移失败会在同一事务中回滚；
- Windows DPAPI 当前用户密钥库：32 字节随机 entropy、版本化 `LCSV` 密文格式、原子替换、当前用户 ACL、引用校验和稳定错误映射；
- Minecraft 头像内容缓存：可信纹理域名校验、无重定向下载、1 MiB 流式上限、严格 PNG 结构/CRC/解压校验、SHA-256 内容寻址和占位回退；
- 账号删除恢复编排：`Deleting` 状态、密钥删除幂等重试、版本引用清理、默认账号清除和启动阶段恢复顺序；
- Avalonia 桌面多账号页：离线/Microsoft 列表、默认和当前版本账号覆盖、正版/离线状态、玩家名删除确认、可取消浏览器登录和本地头像占位展示；
- Microsoft 登录成功后将经过验证的头像缓存写入账号公开资料，UI 只绑定本地缓存路径，不绑定远程纹理地址；
- Microsoft 登录配置与正版登录验收边界文档；
- 文档维护规则，要求用户可见变化同步更新 README 和本文件。

### Changed

- Windows 便携版发布说明改为记录已批准的 Apache-2.0 许可证要求；
- 发布包验证明确要求包含 `LICENSE`，并继续校验第三方声明、SBOM 和文件清单；
- 发布打包阶段会移除依赖带入的 PDB 调试符号，避免调试产物进入发行 ZIP；
- 手动候选发布工作流不再把版本输入插入 PowerShell 命令，发布脚本会拒绝非 SemVer 2.0 版本；
- 公开文档树移除内部开发计划与设计稿，Windows 验收文档改为只保留模板，不记录机器路径、包哈希或个人信息；
- README 明确区分 M1 已实现能力、暂未配置的生产能力和后续里程碑；
- Desktop 已接入 DPAPI 账号流程、头像本地缓存和多账号 UI；真实 Entra 应用登录验收仍需维护者提供 Public Client 注册，当前保持阻塞。
- 安全加固（代码级）：前轮审计列出的下载重定向、工作流输入、归档解压、安装/恢复、诊断暂存、更新暂存和启动参数持久化问题已完成代码修复；下载、归档、安装、版本目录、诊断和更新路径统一经过受约束的 reparse 检查与原子写入，更新器和游戏启动前会以独占读取句柄绑定已验证的可执行文件。对应自动化测试已通过；但 Windows 10/11 原生 junction/reparse 竞态、部署 ACL、真实更新替换、健康检查和回滚仍需在私有验收环境完成，因此这里不宣称完整安全审计已经结束。

### M1 基线

- 已具备分层 Domain/Application/Infrastructure/Desktop/Platform.Windows 架构和独立 Updater；
- 已具备离线账号、Java 发现与选择、原版安装/启动、版本隔离领域模型、诊断包和签名更新暂存基础；
- 已通过当前仓库的 Release 构建、测试、格式检查和 Git diff 检查；
- GitHub Releases 的真实仓库、更新公钥、`keyId`、离线签名和 Windows 10/11 实机验收仍未完成。

## 维护约定

每次用户可见功能、支持范围、发布流程或安全边界发生变化时，先更新 `Unreleased`，再在对应版本打标签时将条目移动到带日期的版本节。具体规则见 [`docs/README.md`](docs/README.md)。
