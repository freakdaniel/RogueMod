# CLI reference

`roguemod` is the kind-neutral manager for RogueMod packages. All commands that touch a game installation accept `--game <directory>` and an optional `--profile <profile.json>` (defaults to the bundled `deadzone-rogue` profile).

```bash
dotnet run --project src/RogueMod.Cli -c Release -- <command> [options]
```

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Operation failed (I/O, invalid argument, invalid manifest) |
| 2 | `diagnose` found an incompatible installation |
| 64 | Unknown command |

> [!NOTE]
> `roguemod list` distinguishes `Enabled`, `Disabled`, and `Broken` deployments. A broken state usually means a UE4SS deployment was deleted externally; `enable` repairs it from the canonical store.

## roguemod new

Scaffolds a complete standalone starter for any package kind without copying repository files.

```bash
roguemod new <managed|lua|native|pak> --id <package-id> [options]
```

| Option | Applies to | Description |
| --- | --- | --- |
| `--id <package-id>` | all | Stable lowercase package id (required) |
| `--name <project-name>` | all | Project/directory name; derived from the id by default |
| `--display-name <name>` | all | Human-readable mod name; defaults to the project name |
| `--loader-id <name>` | lua, native | Immutable UE4SS loader directory name; derived from the project name by default |
| `--output <directory>` | all | Output directory; defaults to `./<project-name>` |
| `--sdk-version <version>` | managed | `RogueMod.Sdk` version; defaults to 0.1.0 |
| `--game-sdk-version <version>` | managed | Generated game SDK version; defaults to 0.1.0 |

> [!IMPORTANT]
> `loaderId` cannot be changed after installation. The installer rejects replacement that changes it.

### Managed next steps

```powershell
roguemod new managed --id example.hello-deadzone
cd Example.HelloDeadzone
dotnet restore --source E:\RogueModFeed
dotnet build -c Release -t:PackageRogueMod
```

### Lua next steps

```powershell
roguemod new lua --id example.hello-lua
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package './Example.HelloLua' --replace
```

### Native next steps

```powershell
roguemod new native --id example.hello-native
cmake -S Example.HelloNative -B Example.HelloNative/.build -A x64
cmake --build Example.HelloNative/.build --config Release --target PackageRogueNativeMod
```

### Pak next steps

```powershell
roguemod new pak --id example.hello-pak
repak pak content paks/example.hello-pak.pak
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package './Example.HelloPak' --replace
```

## roguemod install

Installs any package kind from a package directory and deploys it transactionally.

```bash
roguemod install --game <directory> --package <directory> [--replace] [--profile <profile.json>]
```

> [!NOTE]
> Without `--replace`, installing over an existing package fails. `--replace` preserves nothing except the manifest identity: contents are replaced wholesale, and disabled state is preserved on `update` (not on `install --replace`).

## roguemod list

```bash
roguemod list --game <directory> [--profile <profile.json>]
```

Prints `STATE`, `KIND`, `VERSION`, and `ID` for every installed package.

## roguemod enable / disable

```bash
roguemod enable --game <directory> --id <package-id>
roguemod disable --game <directory> --id <package-id>
```

- Managed: writes a `.roguemod-disabled` marker checked by the in-process runtime.
- Native and Lua: deactivates the UE4SS `mods.txt` line; `enable` also repairs missing deployments.
- Pak: removes or recreates the deployed payload files.

## roguemod update

```bash
roguemod update --game <directory> --package <directory> [--profile <profile.json>]
```

Replaces an installed package with a newer version and preserves its disabled state.

## roguemod uninstall

```bash
roguemod uninstall --game <directory> --id <package-id>
```

Removes the canonical package and every deployment.

## roguemod install-runtime

```bash
roguemod install-runtime --game <directory> --package <directory> [--replace]
```

Installs the runtime payload produced by `scripts/package-runtime.*` under `<GameRoot>/RogueMod`, deploys the UE4SS bridge, and migrates legacy layouts. See [Package manager](mod-manager.md).

## roguemod install-managed / install-native

Legacy single-kind install commands, still supported:

```bash
roguemod install-managed --game <directory> --package <directory> [--replace]
roguemod install-native --game <directory> --package <directory> [--replace]
```

## roguemod diagnose

```bash
roguemod diagnose --game <directory> [--profile <profile.json>]
```

Runs compatibility checks (game version, UE4SS installation, runtime payload, `VTableLayout.ini`) and prints `PASS`/`WARN`/`FAIL` per check. Exits with code 2 when the installation is incompatible.

> [!IMPORTANT]
> On Linux, Steam must launch the game with `WINEDLLOVERRIDES=dwmapi=n,b`. RogueMod reports the required value but never modifies Steam launch options.

## roguemod generate-sdk

Maintainer-only: generates the typed C# game SDK from a UE4SS `.jmap` reflection dump.

```bash
roguemod generate-sdk (--jmap <file> | --game <directory>) --output <directory>
    [--namespace <name>] [--package-id <id>] [--package-version <version>]
    [--roguemod-version <version>] [--game-version <version>]
    [--standalone | --abstractions-project <file>] [--profile <profile.json>]
```

Emits `RogueMod.GameSdk.g.cs` (with XML documentation comments on every wrapper), a source manifest with the dump SHA-256, and a buildable `DeadzoneRogue.Sdk.csproj`. See [Generated SDK](generated-sdk.md) and the maintainer capture flow in the same page.
