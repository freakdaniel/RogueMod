# RogueMod package manager

RogueMod keeps one canonical copy of every package under `<GameRoot>/Mods/<package-id>`. The same commands manage Managed, Native, Lua, and Pak packages; the manager selects the installer from `mod.json`.

`<GameRoot>/Mods` contains actual game mods only. RogueMod infrastructure is installed under `<GameRoot>/RogueMod`; the UE4SS `RogueModBridge` directory contains only its required native bootstrap.

Close the game before installing, updating, enabling, disabling, or uninstalling a mod. Managed assemblies and Pak files may be open while the game is running.

## Commands

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package '.\MyModPackage'
roguemod list --game 'E:\Steam\steamapps\common\Deadzone Rogue'
roguemod disable --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.my-mod
roguemod enable --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.my-mod
roguemod update --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package '.\MyModPackage-v2'
roguemod uninstall --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.my-mod
```

`install` refuses an existing package unless `--replace` is supplied. `update` is deliberately local: it requires an already installed package with the same ID and kind, then replaces it from the supplied directory. Updating a disabled mod preserves its disabled state. Remote catalog/version discovery is not part of the current manager.

The older `install-managed` and `install-native` commands remain available for compatibility. New tooling should use the kind-neutral `install` command.

## States

`list` reports one of three activation states:

- `Enabled`: the activation marker and required deployment files are present;
- `Disabled`: the package remains installed but is intentionally inactive;
- `Broken`: activation requests loading the mod, but its deployment is missing or incomplete.

Enabling a Native or Lua package repairs a missing deployment from the canonical store. Enabling a Pak package recreates its deployed payload. Managed packages use a `.roguemod-disabled` marker that the in-process runtime checks before loading assemblies.

## Managed package

```json
{
  "id": "example.managed",
  "name": "Example managed mod",
  "version": "1.0.0",
  "kind": "managed",
  "entryPoint": "dlls/Example.Managed.dll::Example.Managed.Mod"
}
```

Managed packages are loaded directly from the canonical store. No second deployment copy is made.

## Shared metadata

Every package kind supports the same optional presentation and localization fields. See [Mod manifest](mod-manifest.md) for the complete schema, media rules, and canonical language IDs.

## Native package

```json
{
  "id": "example.native",
  "name": "Example native mod",
  "version": "1.0.0",
  "kind": "native",
  "entryPoint": "dlls/main.dll",
  "loaderId": "ExampleNative"
}
```

Native packages are transactionally deployed to `ue4ss/Mods/<loaderId>` and activated through `ue4ss/Mods/mods.txt`. `loaderId` cannot change during replacement because it is the stable UE4SS identity.

## Lua package

```json
{
  "id": "example.lua",
  "name": "Example Lua mod",
  "version": "1.0.0",
  "kind": "lua",
  "entryPoint": "Scripts/main.lua",
  "loaderId": "ExampleLua"
}
```

Lua packages use the same transactional UE4SS deployment and activation model as Native packages. The entry point is fixed to `Scripts/main.lua`.

## Pak package

```json
{
  "id": "example.pak",
  "name": "Example Pak mod",
  "version": "1.0.0",
  "kind": "pak",
  "entryPoint": "paks/Example.pak"
}
```

The primary entry point must be a `.pak`. Optional `.utoc`, `.ucas`, and `.sig` files with the same base name are deployed alongside it. The Deadzone: Rogue profile targets `Valhalla/Content/Paks/~mods`; deployment filenames include a stable package-ID hash to prevent collisions between packages.

## Transactions and ownership

Install, replacement, and uninstall stage or rename both the canonical package and its external deployment before committing. A failed operation restores the previous package, UE4SS `mods.txt`, and deployment files. Packages containing symbolic links or paths escaping their root are rejected. RogueMod also refuses to replace an existing UE4SS directory that does not contain a matching RogueMod manifest.
