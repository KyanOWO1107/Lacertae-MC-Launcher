# 贡献指南

感谢参与 Lacertae。项目目前以 Windows 10/11 x64 为第一目标，同时保持跨平台架构；贡献应优先保持边界清晰、资源占用可控和普通玩家默认路径简单。

## 开始之前

- 先阅读 [`README.md`](README.md)、[`docs/README.md`](docs/README.md) 和相关 ADR；
- 对架构、许可证、认证、下载源、更新信任根或数据布局的改变，先提交说明或讨论，不要在实现中隐式改变约定；
- 不复制 HMCL、PCL2/PCL-CE、SJMCL 或其他 GPL 项目的代码、资源或受保护表达。它们只能作为行为和交互参考；
- 不提交 OAuth 凭据、访问令牌、更新签名私钥、真实用户数据、私有服务器地址或本地诊断包。

## 代码与文档变更

- 保持 Domain、Application、Infrastructure、Platform.Windows、Desktop 和 Updater 的依赖方向；
- 新行为应先补充能够失败的测试，再实现最小变更；
- 用户可见变化同时更新 README 和 [`CHANGELOG.md`](CHANGELOG.md)；架构决策写入 `docs/decisions/`；
- 依赖版本或许可证变化同时更新 `Directory.Packages.props`、锁定文件和第三方声明文档；
- 发布和更新相关变更必须说明签名、哈希、回滚和失败路径，不得把候选构建描述成生产发布。

## 本地验证

在 Windows PowerShell 中运行：

```powershell
./eng/verify.ps1
```

发布脚本只生成未签名的 Windows 便携候选包。真实签名私钥不得进入仓库、GitHub Actions、ZIP 或客户端；GitHub Releases 发布需要经过维护者批准的离线签名流程。

## 提交约定

提交信息建议使用 `feat:`、`fix:`、`docs:`、`test:`、`build:` 或 `refactor:` 前缀，并保持一次提交聚焦一个可审查主题。提交前请确认 `git diff --check` 通过，且没有把 `artifacts/`、`bin/`、`obj/` 或本地配置加入版本控制。

## 许可证

核心代码按 [`Apache License 2.0`](LICENSE) 发布。提交贡献即表示你有权提交相关内容，并同意在 Apache-2.0 条款下授权该贡献；若贡献包含第三方代码或素材，请在提交说明中提供来源和许可证。官方名称、Logo、主题资源和更新渠道不因代码许可证自动获得使用授权。
