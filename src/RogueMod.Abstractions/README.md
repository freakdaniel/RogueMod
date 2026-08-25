# RogueMod.Abstractions

Stable managed contracts for C# mods targeting Deadzone: Rogue through RogueMod.

Normally mod projects reference `RogueMod.Sdk`, which brings this package transitively and adds the packaging target. Reference this package directly only when you need the managed API without the authoring targets:

```xml
<PackageReference Include="RogueMod.Abstractions" Version="0.1.0" />
```

Implement `IRogueMod` for asynchronous load and unload, and optionally implement `IRogueModGameEvents` for callbacks dispatched on the Unreal game thread. Runtime services are available through `IModContext`.

The package targets .NET 10 because RogueMod hosts a private .NET 10 runtime inside the game process.
