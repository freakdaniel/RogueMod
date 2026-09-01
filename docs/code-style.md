# Code style

Conventions enforced or expected in this repository. `TreatWarningsAsErrors` is on for every project; the build is the first reviewer.

## C#

### Structure

- File-scoped namespaces, one primary type per file where practical.
- `nullable` enable, `ImplicitUsings` enable, `LangVersion latest` (set in `Directory.Build.props`).
- Prefer `sealed` classes, `record`/`record struct` for data, `readonly` structs for transport values.
- Expression-bodied members for one-liners; braces otherwise.
- No `#region`, no partial files unless generated or required by source generators.

### Comments

> [!IMPORTANT]
> Do not add comments explaining *what* code does; the code should say it. Comments are reserved for *why* — build-specific constraints, ABI pinning, and game-verified behavior that cannot be inferred from the code.

XML documentation comments on public API are required (the build enforces this for `RogueMod.Abstractions`, `RogueMod.Sdk`, and the generated SDK) and are the source for the API reference site section.

### Naming

- Types and public members: PascalCase. Locals/parameters: camelCase. Private fields: camelCase without prefix.
- Generated identifiers avoid collisions through `UniqueIdentifier`; keywords are escaped with `@`.
- Test method names end in `Test` and read as behavior: `ManagedPackageInstallsTransactionallyTest`.

### Error handling

- Fail fast with descriptive exceptions (`ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrWhiteSpace`).
- No silent `catch { }` in shipped code. Samples may catch to stay alive across game builds, but must log.
- Transactional filesystem operations stage, then move, and roll back on failure — follow the installer patterns in `RogueMod.Core`.

### Tests

- xUnit v3 (`xunit.v3.mtp-off`); plain `[Fact]`s; the shared local helper `Assert(condition, message)` keeps failure messages descriptive.
- Use `TemporaryDirectory` for filesystem fixtures; never write outside the test's temp root.
- Packaging tests run the real `dotnet` CLI — keep them hermetic (own feed directory per test).

## C++ (bridge and native mods)

- Four-space indent, namespace-scoped braces on their own lines (see `RogueMod.Native`).
- The bridge pins RE-UE4SS ABI with `static_assert` size checks; any layout-dependent change must update the assert together with the code.
- No raw Unreal object pointers cross the managed boundary; only index/serial handles.
- Windows-only: MSVC/`clang-cl` targets, C++23, `UNICODE`/`NOMINMAX`/`WIN32_LEAN_AND_MEAN` defined by CMake.
- No MinGW: its C++ ABI is incompatible with the MSVC-built UE4SS.

## Lua (templates)

- Template Lua stays minimal: log on load, commented examples for the UE4SS API, no speculative helpers.
- RogueMod adds no Lua runtime layer; templates must point at UE4SS documentation rather than wrap it.

## JSON (manifests, profiles)

- `mod.json` uses camelCase and the exact schema documented in [Package manifest](mod-manifest.md).
- Package ids: lowercase, 3-64 chars of `[a-z0-9._-]`. `loaderId`: `[A-Za-z][A-Za-z0-9_]{2,63}`.

## Documentation articles

- Markdown with GitHub-flavored alerts for callouts:

```markdown
> [!NOTE]
> Supplementary information.

> [!IMPORTANT]
> Something that breaks behavior if ignored.
```

- Fenced code blocks always carry a language tag (`powershell`, `bash`, `csharp`, `lua`, `xml`, `json`, `text`).
- Present tense, imperative voice; concrete paths over vague descriptions.
- Pages live in `docs/`, are wired into `docs/toc.yml`, and must render correctly through `scripts/build-docs.*` before merge.

## Generated code

- `RogueMod.GameSdk.g.cs` is never hand-edited; it is regenerated from the JMAP dump by the CLI.
- XML documentation comments for generated types are produced by `CSharpSdkGenerator.WriteDoc` — extend that emission when adding generated surface.
- The generated project sets `<GenerateDocumentationFile>true</GenerateDocumentationFile>` with no `NoWarn` exemptions: every generated public member — including positional record parameters, enum values, hook delegates, and the vector `ToString` override — must carry documentation, and the build fails otherwise.
