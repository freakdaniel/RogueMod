# Contributing

Thank you for considering a contribution to RogueMod. This page covers the practical workflow; code conventions live in [Code style](code-style.md).

## Scope

RogueMod is a mod **loader, manager, and SDK** for Deadzone: Rogue. It provides packaging, deployment, a managed runtime, a reflection bridge, and authoring tooling for C#, C++, Lua, and Pak mods.

> [!IMPORTANT]
> RogueMod does not host gameplay frameworks or hand-written gameplay libraries. Game-specific knowledge is documented (see [Gameplay hook notes](generated-sdk.md#gameplay-hook-notes)) rather than turned into maintained gameplay code. Samples exist only to exercise SDK features.

RogueMod does not disable or bypass anti-cheat, and contributions must not add such functionality.

## Development setup

Requirements for managed work:

- the .NET SDK pinned by `global.json`;
- nothing else — the managed solution is fully self-contained.

Requirements for native work are documented per platform in [Windows development](windows-development.md) and [Linux development](linux-development.md).

```bash
dotnet tool restore
dotnet build RogueMod.slnx -c Release
dotnet test RogueMod.slnx -c Release
```

> [!NOTE]
> The test suite includes end-to-end NuGet packaging tests that invoke `dotnet pack` and `dotnet build` in temp directories. First runs may be slow while the SDK warms its caches.

## Repository layout

```text
src/
  RogueMod.Abstractions/       Stable API referenced by C# mods
  RogueMod.Cli/                Command-line manager
  RogueMod.Core/               Profiles, manifests, installers, diagnostics, scaffolders
  RogueMod.Native/             UE4SS bridge (C++/CMake)
  RogueMod.Runtime/            In-process managed runtime
  RogueMod.Sdk/                JMAP generator, packaging targets, native headers
  RogueMod.Templates/          dotnet new templates for all package kinds
  RogueMod.Sample.*/           Samples exercising the SDK (one feature each)
  RogueMod.Tooling.SdkDumper/  UE4SS Lua dump helper
  RogueMod.Tooling.ApiDocsGen/ API reference markdown generator for the docs site
tests/                         Automated tests and fixtures
docs/                          Documentation guides (Docusaurus docs content)
website/                       Documentation site engine (Docusaurus)
config/                        Game profiles and compatibility data
scripts/                       Reproducible build and packaging entry points
```

## Workflow

1. Fork or branch from `master`.
2. Make the change with tests. Bug fixes and new features need coverage; the existing suite shows the expected style.
3. `dotnet build RogueMod.slnx -c Release && dotnet test RogueMod.slnx -c Release` must pass with zero warnings — warnings are errors in this repository.
4. Update documentation when behavior or public surface changes. Articles live in `docs/`; API docs come from XML documentation comments on the public surface.
5. Open a pull request with a clear description of the behavior change.

### Commit messages

History uses short, capitalized prefixes:

```text
Feat: Add working damage controling
Fix: Windows temp directory dll disposing in tests
Refactor: Centralize runtime, mods, sdk & add Lua/Pak support
```

Use `Feat:`, `Fix:`, `Refactor:`, `Docs:`, or `Test:` followed by an imperative summary.

## Testing expectations

- Core installer/manager behavior: plain xUnit-style tests in `tests/RogueMod.Tests/Program.cs` using the local `Assert(condition, message)` helper and `TemporaryDirectory`.
- Packaging behavior: `NuGetPackageTests` builds real packages with the real `dotnet` CLI.
- Generated SDK output: `StructInheritanceGenerationTests` and friends assert on generated source text.
- Live-game behavior is never required for a PR; bridge-level transport tests run against fixtures, and game-confirmed behavior is recorded in [Reflection status](reflection-api.md).

## Documentation

- Every public member of `RogueMod.Abstractions` must carry XML documentation comments; warnings are errors, so the build enforces this.
- The generated SDK emits its own XML documentation comments from `CSharpSdkGenerator` — extend the emission there, not in hand-written pages, when adding generated surface.
- Articles use GitHub-style alerts (`> [!NOTE]`, `> [!IMPORTANT]`, `> [!WARNING]`) and fenced code blocks with language tags.

## Native components

The bridge (`RogueMod.Native`) is pinned to a RE-UE4SS commit; its ABI surface is validated by `NativeBootstrapValidatesAbiTest` and pinned `static_assert`s. Changing the managed/native ABI requires bumping the host ABI version and updating the pinned headers in `RogueMod.Sdk/Native` together.
