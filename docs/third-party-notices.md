# 第三方声明生成说明

发布包中的 `THIRD-PARTY-NOTICES.txt` 来自当前锁定的 NuGet 图谱和人工复核的许可证网址。直接依赖清单见 [`docs/third-party-dependencies.md`](../third-party-dependencies.md)；发布流程必须同时检查传递依赖，不能只凭该表宣称完整。

项目没有复制 HMCL、PCL2、PCL-CE、SJMCL 或其他 GPL 项目代码。它们只用于行为和交互研究，不进入依赖图或发布包。

许可证文本、版权归属和 SBOM 是发布资产的一部分。根目录项目 `LICENSE` 仍需要所有者批准后才能生成发布候选包。
