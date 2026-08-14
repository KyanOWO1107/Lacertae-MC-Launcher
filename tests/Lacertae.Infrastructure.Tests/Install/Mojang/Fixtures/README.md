# Mojang vanilla metadata fixtures

These fixtures preserve the current Mojang metadata shape while keeping only the
release, libraries, logging configuration, asset index, and two asset objects
needed by the parser tests.

- Version manifest: https://piston-meta.mojang.com/mc/game/version_manifest_v2.json
  - Retrieved: 2026-08-14T05:36:10Z
  - Original SHA-256: `63f5ae8cad0dcb209afffd7b54580e0a0aee167c577cda2739e24398eaf996bf`
- Version metadata: https://piston-meta.mojang.com/v1/packages/c13e92ba70ee9db6ba69c89e8f3831388d6b06c6/1.21.8.json
  - Retrieved: 2026-08-14T05:36:10Z
  - Original SHA-256: `726d0765d61f924364e4658456addca51ee37aa2f8ce34a1ea47fbf31232ad3a`
- Asset index: https://piston-meta.mojang.com/v1/packages/6e351e1de5bdfca8be9997367a120925cfc09ae4/26.json
  - Retrieved: 2026-08-14T05:36:10Z
  - Original SHA-256: `527ed2184ca2c779645d2b8cb7af477bc53158b58981556f5f25f14dae3737c0`

The version manifest entry retains the official metadata SHA-1
`c13e92ba70ee9db6ba69c89e8f3831388d6b06c6`. The injected test source skips
network-body verification for reduced fixtures; production HTTP responses are
verified against that SHA-1 before an artifact is planned.
