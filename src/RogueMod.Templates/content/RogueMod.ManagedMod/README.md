# RogueMod.ManagedMod

Sample managed mod is a managed mod for Deadzone: Rogue.

Edit `src/RogueMod.ManagedMod/mod.json` to describe the mod, its media, supported languages and localized descriptions. Put icon and gallery files under `media/` beside the manifest.

## Build the mod package

```powershell
dotnet restore
dotnet build -c Release -t:PackageRogueMod
```

The package is written to `.artifacts/packages/managed/Release/sample.managed-mod`. Install it with:

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package '.artifacts\packages\managed\Release\sample.managed-mod' --replace
```

`RogueMod.Sdk` supplies the authoring API and packaging target. `DeadzoneRogue.Sdk` is compile-only; its large assembly is installed once with the RogueMod runtime and is not copied into each mod.
