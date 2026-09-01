# RogueMod documentation

RogueMod is a C#-first mod loader and manager for Deadzone: Rogue, built on RE-UE4SS. It supports four package kinds — managed C#, native C++, Lua, and Unreal Pak — with a stable managed API, a generated typed game SDK, and one kind-neutral CLI for installing and managing every package.

> [!IMPORTANT]
> Deadzone: Rogue 1.4.2.0 / Unreal Engine 5.6.1 is the verified game build. The reflection layer is live-tested in the installed game; see [Reflection status](reflection-api.md) for the supported type matrix.

## Where to start

| Goal | Page |
| --- | --- |
| Create a C# mod | [Managed mod quick start](creating-managed-mod.md) |
| Create a Lua mod | [Lua mod quick start](creating-lua-mod.md) |
| Create a C++ mod | [Native mod quick start](creating-native-mod.md) |
| Distribute cooked content | [Pak mod quick start](creating-pak-mod.md) |
| Browse every CLI command | [CLI reference](cli-reference.md) |
| Understand the design | [Architecture](architecture.md) |

## Package kinds

| Kind | Runtime | Authoring |
| --- | --- | --- |
| Managed | In-process CoreCLR, one collectible `AssemblyLoadContext` per mod | `RogueMod.Sdk` + generated `DeadzoneRogue.Sdk` |
| Native | UE4SS C++ DLL | Pinned lifecycle headers via `RogueMod.Sdk` |
| Lua | UE4SS Lua runtime | UE4SS Lua API; RogueMod owns packaging |
| Pak | Unreal pak mount | `repak` or UnrealPak; RogueMod owns deployment |

## The documentation stack

- **Guides** (these pages) — quick starts, the managed API walkthrough, packaging, architecture, and development docs.
- **API reference** (auto-generated) — full reference for `RogueMod.Abstractions` and `RogueMod.Sdk`, generated from XML documentation comments by `RogueMod.Tooling.ApiDocsGen` into Docusaurus pages under `/api`. The generated `DeadzoneRogue.Sdk` reference is produced from the same XML comments the generator embeds into every wrapper, property, and hook, so mod authors get identical docs through IntelliSense and this site.

> [!NOTE]
> To build the documentation site locally, run `npm install` once in `website/`, then `npm run dev` for a live preview or `npm run build` for the static output in `website/build`. The API reference is regenerated with `npm run gen-api` (requires a Release build of the solution).
