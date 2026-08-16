# Microsoft 登录验收与配置边界

当前阶段完成 Microsoft 公共客户端配置读取、严格校验、Infrastructure 认证适配器边界、Application 账号编排、Windows DPAPI 密钥库、头像内容缓存和 Desktop 多账号页接线；真实 Entra 应用登录仍需维护者提供 Public Client 注册，不把当前构建描述为“已完成正版登录”。

仓库根目录的 `.entra-id` 是维护者从 Entra Portal 导出的本地审核资料，包含显示名称、Application/Client ID、Object ID 和 Tenant ID。它只用于准备 AppID Review 表单，已加入 Git 忽略，不能进入源码、诊断包或发行 ZIP。运行 `eng/prepare-oauth-local.ps1` 会从该资料生成启动器实际读取的 `<可执行文件目录>/oauth.local.json`；运行时文件只包含 `clientId` 和固定 `consumers` authority，不复制 Tenant ID、Object ID 或任何 secret。

适配器使用 `CmlLibMicrosoftIdentityClient`，内部明确构造 MSAL 公共客户端、`https://login.microsoftonline.com/consumers` authority 和 `http://localhost` system-browser 回调。每次操作只创建一个内存中的 CmlLib `JEGameAccount`，不调用 `JELoginHandlerBuilder.BuildDefault()`，也不使用其内置 Client ID；MSAL 缓存通过回调从当前账号的短期字节材料加载/序列化。Xbox provider 固定使用 `rp://api.minecraftservices.com/`，Java Edition 链显式开启 ownership checker。

## 配置来源

启动器按以下顺序读取配置：

1. 环境变量 `LACERTAE_MICROSOFT_CLIENT_ID`；
2. 启动器可执行文件所在目录的 `oauth.local.json`；
3. 两者都不存在时，Microsoft 登录显示为“此构建未配置”，离线账号不受影响。

`oauth.local.json` 只允许以下字段：

```json
{
  "clientId": "00000000-0000-0000-0000-000000000000",
  "authority": "https://login.microsoftonline.com/consumers"
}
```

`clientId` 必须是非空、非全零 GUID。`authority` 可省略，默认固定为消费者租户地址；如果提供，只接受该 HTTPS 地址及其末尾 `/` 形式。重定向地址固定为 MSAL.NET system-browser 合约 `http://localhost`，不从文件或环境变量读取。

以下字段会被拒绝：`redirectUri`、`clientSecret`、证书、密码、未知字段和重复字段。配置解析失败只显示稳定问题代码，不显示 Client ID、文件路径或任何秘密。

## Windows DPAPI 缓存边界

`DpapiSecretVault` 只在 Windows 上工作，使用 `DataProtectionScope.CurrentUser` 保护每个账号的短期 MSAL 缓存。文件名只接受 32 位小写十六进制 `secretRef`，内容使用 `LCSV` magic、版本号、32 字节随机 entropy 和 DPAPI 密文；写入先 flush 到磁盘、限制当前用户 ACL，再在同一目录内原子替换最终文件。读取会校验格式、大小和 DPAPI 完整性，篡改或无法解密统一返回 `SECRET_DECRYPT_FAILED`，不回退到明文或 JSON。

该密钥库已在 Windows 平台测试中通过，Desktop 组合根会在 Windows 启动时为账号页创建同一数据根下的 `DpapiSecretVault`。密钥内容不会进入 ViewModel、XAML 绑定、日志或诊断包。

## 当前证据

| 场景 | 状态 | 证据 |
| --- | --- | --- |
| 缺少配置时返回未配置状态，不访问网络 | PASS | `OAuthClientRegistrationLoaderTests.MissingEnvironmentAndFileReturnsUnconfiguredWithoutAProblem` |
| 环境变量优先于本地文件 | PASS | `OAuthClientRegistrationLoaderTests.EnvironmentClientIdTakesPrecedenceOverTheLocalFile` |
| 本地文件只接受公共 Client ID 和消费者 authority | PASS | `OAuthClientRegistrationLoaderTests.LocalFileAcceptsOnlyPublicClientIdAndAuthority` |
| secret、redirect 和未知字段被拒绝 | PASS | `OAuthClientRegistrationLoaderTests.LocalFileRejectsSecretsRedirectsAndUnknownFields` |
| 真实机器便携包预检 | NOT RUN | 机器、包和证据数据保存在维护者私有验收记录中 |
| 缺少 Client ID 时不触发浏览器或后端 | PASS | `CmlLibMicrosoftIdentityClientTests.MissingClientIdReturnsNotConfiguredWithoutCallingBackend` |
| MSAL 使用公共客户端、消费者 authority 和精确 loopback 回调 | PASS | `CmlLibMicrosoftIdentityClientTests.MsalApplicationUsesOnlyPublicClientAndExactLoopbackRedirect` |
| CmlLib 链显式开启 Java Edition 所有权检查 | PASS | `CmlLibMicrosoftIdentityClientTests.CmlLibPipelineEnablesJavaEditionOwnershipCheck` |
| 认证结果不暴露 token，缓存材料可清零 | PASS | `CmlLibMicrosoftIdentityClientTests.SuccessfulBackendResultMapsToLacertaeOnlySessionAndRedactsSecrets` |
| 添加账号先写秘密、数据库失败后清理；刷新仅在成功后替换缓存 | PASS | `AccountSessionOrchestrationTests.AddMicrosoftWritesSecretBeforeProfileAndCleansItWhenProfileWriteFails`、`RefreshWritesRotatedCacheOnlyAfterSuccessfulRefresh` |
| 版本账号覆盖优先且无有效选择时不静默降级 | PASS | `AccountSessionOrchestrationTests.ResolvePrefersActiveVersionOverrideOverActiveDefault`、`ResolveReturnsAccountRequiredForMissingOrInactiveSelection` |
| 真实 Entra 应用注册、浏览器回调和 Java Edition 资料 | BLOCKED | 尚未提供 Lacertae 自有 Public Client 注册 |
| MSAL 静默刷新编排和 Windows DPAPI 缓存 | PASS | `AccountSessionOrchestrationTests`、`DpapiSecretVaultTests` 覆盖刷新写入顺序、`LCSV` 密文、篡改失败、原子替换和当前用户 ACL |
| 头像下载和本地缓存不影响账号流程 | PASS | `PngValidatorTests`、`HttpAvatarCacheTests` 覆盖可信域名、重定向、内容类型、1 MiB 限制、PNG CRC/解压边界和占位回退 |
| Desktop 账号页只绑定本地头像路径，默认/版本覆盖更新摘要且登录可取消 | PASS | `AccountsViewModelTests`、`MainWindowTests.ConfiguredAccountsRouteRendersDedicatedAccountPanel` |
| Microsoft 登录成功后只保存已校验的 64 位十六进制头像缓存键 | PASS | `AccountSessionOrchestrationTests.AddMicrosoftStoresOnlyTheValidatedLocalAvatarCacheKey` |
| 删除账号后秘密不可恢复 | PASS | `DeleteAccountTests`、`RecoverAccountDeletionsTests`、`StartupCoordinatorTests` 覆盖 `Deleting` 标记、秘密清理失败保留、幂等恢复和启动顺序 |

## 运行确定性测试

```powershell
dotnet test tests/Lacertae.Desktop.Tests/Lacertae.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OAuthClientRegistrationLoaderTests|FullyQualifiedName~OnboardingViewModelTests"

dotnet test tests/Lacertae.Desktop.Tests/Lacertae.Desktop.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AccountsViewModelTests

dotnet test tests/Lacertae.Infrastructure.Tests/Lacertae.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~CmlLibMicrosoftIdentityClientTests
```

真实登录验收前，维护者必须提供本地公共 Client ID，并在 Microsoft Entra 的 **Mobile and desktop applications** 平台登记精确的 `http://localhost`。不得把 `oauth.local.json`、令牌、缓存、账号资料或登录截图提交到 Git、诊断包或发布 ZIP。

准备本地测试配置（不会修改仓库源码）：

```powershell
./eng/prepare-oauth-local.ps1 -ExecutableDirectory artifacts/acceptance/microsoft/package
```

如果目标目录已有配置，脚本默认拒绝覆盖；只有明确确认替换时才使用 `-Force`。发布正式 ZIP 前不要运行到待上传的包目录，避免把本地配置带入发行物。
