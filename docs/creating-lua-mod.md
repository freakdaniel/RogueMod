# Create a Lua mod

Lua mods run inside UE4SS's Lua runtime: the Unreal object model, `RegisterHook`, `FindFirstOf`, and utility globals come from UE4SS itself. RogueMod owns everything around the script — the package manifest, canonical storage, transactional deployment, and activation state. A Lua mod needs no build step and no repository files.

## Scaffold a starter

```powershell
roguemod new lua `
  --id example.hello-lua `
  --name Example.HelloLua `
  --display-name 'Hello Lua'
```

The equivalent installable template is `RogueMod.Templates`:

```powershell
dotnet new roguemod-lua -n Example.HelloLua `
  --mod-id example.hello-lua `
  --mod-name 'Hello Lua'
```

Both paths emit the same standalone layout:

```text
Example.HelloLua/
  mod.json          Package manifest (kind "lua")
  Scripts/main.lua  Entry point executed by UE4SS
  README.md
```

## Manifest

The Lua manifest follows the shared [package manifest](mod-manifest.md) rules with two specifics:

- `entryPoint` is fixed to `Scripts/main.lua`; UE4SS executes that file when the game loads.
- `loaderId` is required and immutable after installation. It must contain 3-64 ASCII letters, digits or `_`, start with a letter, and satisfy UE4SS directory-name constraints. `roguemod new lua` derives it from the project name unless `--loader-id` is supplied.

## Install and manage

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package './Example.HelloLua' --replace
roguemod list --game 'E:\Steam\steamapps\common\Deadzone Rogue'
roguemod disable --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.hello-lua
```

The package is stored canonically under `<GameRoot>/Mods/example.hello-lua` and deployed transactionally to `ue4ss/Mods/<loaderId>`. `enable`, `disable`, `update`, and `uninstall` behave exactly as for every other package kind; see [Package manager](mod-manager.md).

## Scripting

Write gameplay code against the [UE4SS Lua API](https://ue4ss.org/docs). RogueMod does not add a second Lua reflection layer. `Log` output goes to `UE4SS.log`. A minimal example:

```lua
Log("Hello Lua loaded.")

-- RegisterHook("/Script/Valhalla.SomeClass:SomeFunction", function(context, param)
--     Log(context:get():GetFullName())
-- end)
```

Function paths and signatures must match the installed game build; the same [gameplay hook notes](generated-sdk.md#gameplay-hook-notes) that apply to C# mods apply to Lua mods.
