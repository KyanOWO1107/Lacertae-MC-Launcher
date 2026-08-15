# Changelog

本文件记录 Lacertae 的用户可见变化、发布边界和重要维护变化。项目当前处于 M1 开发阶段，尚未发布正式版本；`Unreleased` 中的内容不代表已经生成公开发行包。

## [Unreleased]

### Added

- 根目录 Apache License 2.0，覆盖核心源代码、Updater、更新协议、构建脚本和文档；
- 项目 README、贡献指南和文档索引；
- GitHub Releases 静态更新分发说明，包含 `stable`、`preview`、`test`、`nightly` 渠道边界；
- 文档维护规则，要求用户可见变化同步更新 README 和本文件。

### Changed

- Windows 便携版发布说明改为记录已批准的 Apache-2.0 许可证要求；
- 发布包验证明确要求包含 `LICENSE`，并继续校验第三方声明、SBOM 和文件清单；
- 发布打包阶段会移除依赖带入的 PDB 调试符号，避免调试产物进入发行 ZIP；
- README 明确区分 M1 已实现能力、暂未配置的生产能力和后续里程碑。

### M1 基线

- 已具备分层 Domain/Application/Infrastructure/Desktop/Platform.Windows 架构和独立 Updater；
- 已具备离线账号、Java 发现与选择、原版安装/启动、版本隔离领域模型、诊断包和签名更新暂存基础；
- 已通过当前仓库的 Release 构建、测试、格式检查和 Git diff 检查；
- GitHub Releases 的真实仓库、更新公钥、`keyId`、离线签名和 Windows 10/11 实机验收仍未完成。

## 维护约定

每次用户可见功能、支持范围、发布流程或安全边界发生变化时，先更新 `Unreleased`，再在对应版本打标签时将条目移动到带日期的版本节。具体规则见 [`docs/README.md`](docs/README.md)。
