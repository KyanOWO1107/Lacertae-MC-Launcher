# M1 Windows 验收矩阵

这份矩阵只记录实际证据。每一行必须填写 `PASS`、`FAIL`、`BLOCKED` 或 `NOT RUN`，空白不代表成功。证据目录必须位于明确的 acceptance root 下，不能使用开发者已有的 `.minecraft`、AppData 或工作区数据。

## 环境身份

| 字段 | 值 |
| --- | --- |
| Git SHA | 待运行填写 |
| 包 SHA-256 | 待运行填写 |
| Windows 版本/Build | 待运行填写 |
| 物理机或 VM | 待运行填写 |
| 架构 | x64 |
| DPI/缩放 | 待运行填写 |
| Java 安装 | 待运行填写 |
| 网络场景 | 待运行填写 |
| DataRoot 模式 | AppData / portable |
| 证据目录 | 待运行填写 |

公开仓库只保留验收矩阵模板；实际机器、包哈希、Java 路径、磁盘空间和证据目录属于维护者的私有验收记录，不应提交到 Git。运行预检脚本时，请把证据写入明确的私有 acceptance root，并将结果按下表回填为 `PASS`、`FAIL`、`BLOCKED` 或 `NOT RUN`。

## 最低矩阵

| 区域 | 场景 | 状态 | 证据 |
| --- | --- | --- | --- |
| Windows | Windows 10 x64 | NOT RUN | |
| Windows | Windows 11 x64 | NOT RUN | |
| 显示 | 100%、150%、200% 缩放 | NOT RUN | |
| 键盘 | 键盘操作、减少动态效果 | NOT RUN | |
| DataRoot | AppData 默认与 portable 标记隔离 | NOT RUN | |
| 路径 | 空格、非 ASCII、长路径 | NOT RUN | |
| 账号 | 离线添加/切换/删除 | NOT RUN | |
| Java | 兼容发现、不兼容手动路径、缺失后托管安装 | NOT RUN | |
| 版本 | 空目录、安装、修复、显示名/物理文件夹重命名、五种隔离策略 | NOT RUN | |
| 网络 | 离线启动、官方失败后经确认的镜像、取消/恢复、错误哈希 | NOT RUN | |
| 启动 | 离线与已批准的 Microsoft 登录、普通/崩溃/停止 | NOT RUN | |
| 诊断 | 预览、导出、脱敏扫描 | NOT RUN | |
| 路径安全 | junction/reparse 竞态、目标目录 ACL、诊断/安装/更新写入边界 | NOT RUN | 代码级安全测试不替代 Windows 原生对象身份和 ACL 证据 |
| 更新 | 有效签名、错误签名/哈希、替换失败回滚 | BLOCKED | 真实 key/domain 尚未配置 |

运行 `./eng/run-acceptance-preflight.ps1 -AcceptanceRoot <明确目录> -PackagePath <可选 ZIP>` 只做环境检查，不会删除任何真实用户目录。

## 资源预算

在主页空闲 60 秒和四个并行下载期间记录私有工作集、30 秒平均 CPU、UI 调度器最大延迟和总吞吐。M1 初始阈值为：空闲私有工作集不超过 250 MiB、平均 CPU 不超过 1%、无超过 200 ms 的 UI 调度阻塞、下载并发不超过 4。未测量时保持 `NOT RUN`。
