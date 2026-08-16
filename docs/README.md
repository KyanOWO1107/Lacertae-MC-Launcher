# 文档索引与维护规则

这里集中记录 Lacertae 的当前约定、设计决策、发布流程和验收证据。内部开发计划可保留在本地 `docs/superpowers/`，该目录已加入 Git 忽略且不纳入公开文档树；已经改变的当前规则应写入对应的现行文档。

## 文档索引

| 类型 | 文档 | 用途 |
| --- | --- | --- |
| 项目入口 | [`../README.md`](../README.md) | 当前定位、已实现范围、开发验证和用户可见边界 |
| 变更记录 | [`../CHANGELOG.md`](../CHANGELOG.md) | 用户可见功能、发布和重要维护变化 |
| 贡献指南 | [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | 代码、文档、许可证和安全边界 |
| 架构决策 | [`decisions/0001-modular-monolith.md`](decisions/0001-modular-monolith.md) | 分层结构与进程边界 |
| 更新决策 | [`decisions/0002-update-distribution.md`](decisions/0002-update-distribution.md) | 渠道、静态托管、签名和信任根 |
| 发布流程 | [`releasing/windows-portable.md`](releasing/windows-portable.md) | Windows 便携候选包和签名发布前检查 |
| 验收证据 | [`testing/m1-windows-acceptance.md`](testing/m1-windows-acceptance.md) | Windows 10/11 验收矩阵和性能阈值 |
| Microsoft 登录 | [`testing/microsoft-login.md`](testing/microsoft-login.md) | 公共客户端配置门控和正版登录验收边界 |
| 依赖清单 | [`third-party-dependencies.md`](third-party-dependencies.md) | 直接依赖、版本、许可证和来源 |
| 第三方声明 | [`third-party-notices.md`](third-party-notices.md) | 发布包声明与传递依赖复核要求 |
## 维护规则

1. README 只写当前能够由代码、测试或验收证据支持的能力；未配置或未验证的内容必须标记为“未完成”“阻塞”或“后续里程碑”。
2. 用户可见变化、支持范围变化、发布流程变化和安全边界变化，必须在同一变更中更新 `CHANGELOG.md` 的 `Unreleased`。
3. 影响多个模块或改变长期约束的选择写入 `docs/decisions/`，包含背景、决策、后果和状态；不要用 README 代替 ADR。
4. 发布文档必须区分“本地候选包”“已签名资产”和“生产更新”；没有真实公钥、签名流程或验收证据时，不得声称自动更新已具备生产能力。
5. 依赖升级、下载源变更和许可证变化必须同步复核锁定图谱、SBOM、`docs/third-party-dependencies.md` 和 `THIRD-PARTY-NOTICES.txt`。
6. 文档中不写入令牌、私钥、个人路径、真实用户数据或未经批准的生产域名；示例使用占位符或本地相对路径。
7. 版本打标签前，把 `CHANGELOG.md` 的 `Unreleased` 移到带版本号和日期的节，并在新的 `Unreleased` 节中保留后续变化入口。

## 状态词汇

- **已实现**：代码和针对性检查已存在；
- **预览/测试**：实现存在，但仅在显式渠道或小范围验收中开放；
- **阻塞**：因公钥、域名、平台、服务或所有者批准等外部条件尚未完成；
- **规划中**：已进入路线图，但当前版本尚未实现。
