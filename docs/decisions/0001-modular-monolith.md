# ADR 0001: Modular monolith for the first launcher release

- Status: Accepted
- Date: 2026-08-13

## Context

Lacertae targets ordinary Minecraft players, with Windows 10/11 x64 as the first release and a cross-platform architecture reserved for later work. The launcher must remain efficient for a portable distribution while keeping authentication, version installation, Java discovery, game launch, resource downloads and UI concerns replaceable. GPL projects are behavior and interaction references only; their code is not copied.

## Decision

Use one desktop process with six production projects and explicit dependency direction:

```text
Domain
  ↑
Application
  ↑
Infrastructure     Platform.Windows
        \          /
             Desktop

Updater is a separate process and references Domain only.
```

- `Lacertae.Domain` owns immutable records, rules, results and problems.
- `Lacertae.Application` owns use-case contracts and orchestration and references Domain only.
- `Lacertae.Infrastructure` owns CmlLib, SQLite, Serilog and network/file adapters.
- `Lacertae.Platform.Windows` owns Windows-specific credential and OS seams.
- `Lacertae.Desktop` owns Avalonia views, composition and user interaction.
- `Lacertae.Updater` remains a small independent process for staged updates.

No general plugin runtime is part of M1. Extension points are explicit application ports and adapters. The portable executable prefers a sibling `lacertae.portable` marker for local data; otherwise data is stored under the system application-data directory.

## Consequences

- Lower process and memory overhead than a service/plugin split.
- Third-party adapters remain replaceable without leaking their types into Domain or Application.
- Windows seams are explicit, so future Linux/macOS implementations can be added without changing core rules.
- Update replacement is isolated in a separate process, while ordinary launcher operations stay in one process.
- A plugin marketplace and arbitrary runtime extensions are deferred until a concrete compatibility and security model exists.
