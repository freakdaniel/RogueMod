# Typed hooks sample

This sample consumes `RogueMod.Sdk` and `DeadzoneRogue.Sdk` exactly like an external managed mod. Its source contains no hand-written Unreal paths, offsets, function descriptors, or ABI marshalling.

The mod finds the generated `KismetSystemLibrary` class default object, registers generated pre- and post-hook delegates for `ParseCommandLine`, invokes the generated method, and verifies a typed `IReadOnlyDictionary<string, string>` replacement.

From the repository root, prepare the local SDK feed and build the package with:

```bash
scripts/build-game-sdk-samples.sh
```

On Windows, use:

```powershell
./scripts/build-game-sdk-samples.ps1
```

The resulting mod package is written to `.artifacts/packages/managed/Release/sample.typed-hooks`. `DeadzoneRogue.Sdk.dll` is intentionally excluded because the matching assembly is installed once under `RogueMod/runtime/shared`.
