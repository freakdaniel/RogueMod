# Linux development

Both managed and native mod development are supported on Linux. Managed mods compile to portable IL. Native mods are cross-compiled to Windows x64 DLLs for UE4SS to load inside the Proton game process.

## Managed mods

Install the .NET SDK selected by `global.json`, then build the sample:

```bash
dotnet build src/RogueMod.Sample.Managed/RogueMod.Sample.Managed.csproj \
  -c Release -t:PackageRogueMod
```

`PackageRogueMod` writes:

```text
.artifacts/packages/managed/Release/sample.hello-managed/
  mod.json
  dlls/RogueMod.Sample.Managed.dll
```

External mods should reference the published `RogueMod.Abstractions` package. Inside this repository, the sample uses a project reference and the shared target at `src/RogueMod.Sdk/Managed/RogueMod.ManagedMod.targets`.

## Native toolchain

RogueMod is pinned to RE-UE4SS commit `a1e7f571c789f63f3de6773d056be6f778c14dc8`, matching the tested `UE4SS_v3.0.1-1088-ga1e7f571` build.

Install host tools:

```bash
sudo apt install cmake ninja-build clang lld llvm
rustup target add x86_64-pc-windows-msvc
scripts/native-toolchain.sh doctor
```

The bridge itself uses a minimal pinned `CppUserModBase` ABI declaration and does not require UE4SS source. Mods that use full Unreal objects, hooks, reflection, or Lua APIs need the exact UE4SS source and its UEPseudo submodule:

```bash
scripts/native-toolchain.sh fetch-ue4ss
```

GitHub access to Epic-owned Unreal repositories requires the developer account linking and SSH setup documented by UE4SS.

Download the pinned xwin 0.9.0 bootstrap and Microsoft SDK files:

```bash
scripts/native-toolchain.sh bootstrap-xwin --accept-microsoft-license
```

The explicit flag acknowledges the Microsoft component licenses. The xwin archive is SHA-256 verified, and downloaded files remain under ignored `.tools/` directories.

Build the bridge and native sample:

```bash
scripts/native-toolchain.sh build
```

The toolchain uses `clang-cl`, `lld-link`, and the Microsoft CRT/Windows SDK. MinGW is intentionally unsupported because its GNU C++ ABI is incompatible with the installed MSVC UE4SS build.

Outputs are centralized:

```text
.build/native/Game__Shipping__Win64/RogueMod.Bridge.dll
.artifacts/packages/native/Game__Shipping__Win64/sample.hello-native/
```

The native SDK headers are located at `src/RogueMod.Sdk/Native/include`.

## Runtime package for Proton

The managed runtime is framework-dependent but carries a private Windows .NET Runtime so it does not depend on the Proton prefix:

```bash
scripts/package-runtime.sh Release
```

The script downloads the pinned `dotnet-runtime-10.0.10-win-x64.zip`, verifies its SHA-512, publishes managed runtime files, and copies the built bridge into `.artifacts/runtime/RogueMod`.

The same package is installed on Windows and Linux/Proton:

```bash
dotnet run --project src/RogueMod.Cli -c Release -- install-runtime \
  --game "/path/to/Deadzone Rogue" \
  --package ".artifacts/runtime/RogueMod" \
  --replace
```

## Transactional package installation

Install a package through the CLI rather than copying files manually. The installer validates its manifest and entry point, rejects symbolic links, stages all files, and replaces existing content only with `--replace`.

Managed packages are stored directly in `<GameRoot>/Mods/<package-id>`. Native packages are stored there as well and deployed to `ue4ss/Mods/<loaderId>`. Both the canonical native package and its deployment are rolled back if activation fails.
