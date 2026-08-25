# RogueMod templates

Install the template package and create a managed Deadzone: Rogue mod:

```powershell
dotnet new install E:\RogueModFeed\RogueMod.Templates.0.1.0.nupkg
dotnet new roguemod-managed -n MyFirstMod --mod-id my.first-mod --mod-name "My first mod"
```

The generated solution builds a ready RogueMod package with `dotnet build -c Release -t:PackageRogueMod`.

The parameterized starter files are owned by this project under `content/`. Repository sample projects are executable examples and are not duplicated or repackaged as templates.
