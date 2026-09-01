# Create a pak mod

Pak mods are Unreal pak payloads: cooked or loose content shipped as a `.pak` (optionally with IoStore companions). There is no code and no runtime component — RogueMod owns the manifest, canonical storage, hashed deployment into the game's pak directory, and activation state.

## Scaffold a starter

```powershell
roguemod new pak `
  --id example.hello-pak `
  --name Example.HelloPak `
  --display-name 'Hello Pak'
```

The equivalent installable template is `RogueMod.Templates`:

```powershell
dotnet new roguemod-pak -n Example.HelloPak `
  --mod-id example.hello-pak `
  --mod-name 'Hello Pak'
```

Both paths emit the same standalone layout:

```text
Example.HelloPak/
  mod.json      Package manifest (kind "pak")
  README.md
  .gitignore
```

## Pack the payload

Create a `content/` directory and place the files exactly as they should appear inside the pak — for Deadzone: Rogue normally a `Valhalla/Content/...` tree. Then pack with repak (cross-platform):

```powershell
repak pak content paks/example.hello-pak.pak
```

UnrealPak from the matching Unreal Engine install works equally; the result must be a plain `.pak` in `paks/`. The project directory itself becomes the installable package: `entryPoint` is derived from the mod id as `paks/<mod-id>.pak`.

If the payload uses IoStore, place the `.utoc`, `.ucas`, and `.sig` files beside the `.pak` with the same base name. The installer deploys them together and removes them on disable or uninstall.

## Manifest

The pak manifest follows the shared [package manifest](mod-manifest.md) rules. Unlike native and Lua packages there is no `loaderId`: pak payloads deploy to the profile pak directory (`Valhalla/Content/Paks/~mods`) with a stable package-id hash in the deployed file name, so two pak mods can never collide.

## Install and manage

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package './Example.HelloPak' --replace
roguemod list --game 'E:\Steam\steamapps\common\Deadzone Rogue'
roguemod disable --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.hello-pak
```

The canonical package is stored under `<GameRoot>/Mods/example.hello-pak`. `roguemod disable` removes the deployed payload files; `enable` recreates them from the canonical store; `update` and `uninstall` behave exactly as for every other package kind. See [Package manager](mod-manager.md).
