# RogueMod.Sdk

Authoring package for managed and native Deadzone: Rogue mods built for RogueMod.

## Managed mod

Add the package and declare the package identity in the project file:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <RogueModModId>example.my-mod</RogueModModId>
  <RogueModModName>My mod</RogueModModName>
  <RogueModEntryPoint>Example.MyMod</RogueModEntryPoint>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="RogueMod.Sdk" Version="0.1.0" />
</ItemGroup>
```

`RogueMod.Sdk` brings `RogueMod.Abstractions` transitively. Implement `IRogueMod`, then build the ready-to-install directory:

```shell
dotnet build -c Release -t:PackageRogueMod
```

The default output is `.artifacts/packages/managed/<Configuration>/<RogueModModId>`. Set `RogueModPackageOutput` to override it.

Add `DeadzoneRogue.Sdk` when the mod uses generated game types. Its assembly is a compile-time reference and is intentionally omitted from the mod package. The compatible DLL is installed once with RogueMod runtime and shared by all managed mods, so mod size does not grow by the size of the generated SDK.

## Native headers

The package contains the pinned minimal UE4SS lifecycle headers under `build/native/include`. MSBuild consumers can use the absolute `$(RogueModNativeIncludeDir)` property added by the package.

These headers cover lifecycle callbacks and the RogueMod export macro only. Mods using Unreal objects, hooks, reflection, or Lua APIs still need the complete UE4SS SDK from the pinned compatible commit.
