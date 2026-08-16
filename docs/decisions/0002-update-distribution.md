# 更新分发与渠道决策

状态：已确认算法，生产地址和公钥待配置。

## 渠道

更新清单中的 `channel` 使用四个固定值：

- `stable`：面向普通用户的稳定版，默认且唯一的生产默认渠道；
- `preview`：候选版，用于接近发布的验证；
- `test`：测试版，用于小范围测试用户；
- `nightly`：每日构建，仅在构建配置显式启用时可见。

客户端不接受用户输入的 manifest URL，也不根据渠道拼接任意 URL。每个渠道由发布配置映射到预先配置的 HTTPS 清单和 detached signature 地址，清单内的渠道值还必须与请求渠道一致。`preview`、`test` 和 `nightly` 不会因为服务器返回内容而自动开放。

## 分发入口

M1 不要求自建更新应用服务器。GitHub Releases、GitHub Pages、Cloudflare Pages/R2、对象存储或 CDN 均可作为静态文件托管，只要提供稳定 HTTPS 地址并发布：

1. `manifest.json`；
2. `manifest.sig`；
3. 清单中声明的 ZIP 包；
4. 可选的公开 release notes 页面。

客户端只信任内置的 ECDSA P-256 SPKI 公钥，并独立校验清单签名、ZIP 大小、ZIP SHA-256 和包内文件清单。GitHub 的仓库权限、Release 编辑权限或 CDN 缓存被攻破时，攻击者仍不能生成客户端接受的更新，除非同时取得离线签名私钥。

初期建议使用 GitHub Releases 资产作为实际文件源，避免维护额外后端；清单和签名可以同样作为 Release 资产。对外稳定入口可在后续增加自有域名/CNAME/CDN，客户端只需更新内置的静态地址映射并发布一次正常签名版本。域名迁移不是信任根迁移，公钥和 `keyId` 仍保持独立。

主页与更新分发明确分离：维护者提供的云服务器和域名可以承载启动器主页、文档、公告和下载说明，但由于其带宽不作为 ZIP/更新资产源。M1 仍使用 GitHub Releases 承载 `manifest.json`、签名和 ZIP；在收到确切域名、DNS 和部署方式前，客户端不写入任何生产主页或更新地址。

## 未启用项

当前没有真实生产域名、`keyId` 或 SPKI DER 公钥，因此生产更新保持禁用。CI 只生成未签名包和清单；离线签名完成后才可发布资产。GitHub Actions 不保存或访问签名私钥。
