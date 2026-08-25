# RogueMod manifest

Every real mod is a self-contained directory with a `mod.json`. Identity and loading fields are required; presentation fields are optional so existing packages remain compatible.

```json
{
  "id": "example.localized-mod",
  "name": "Localized example",
  "version": "1.0.0",
  "kind": "managed",
  "entryPoint": "dlls/Example.dll::Example.Mod",
  "description": "English description shown by mod managers.",
  "icon": "media/icon.webp",
  "images": ["media/screenshot-1.webp", "media/screenshot-2.webp"],
  "defaultLanguage": "en",
  "supportedLanguages": ["en", "ru", "uk"],
  "localizations": {
    "ru": { "description": "Описание на русском." },
    "uk": { "description": "Опис українською." }
  }
}
```

`description` is written in `defaultLanguage`. `localizations` may override the description for any entry in `supportedLanguages`. An icon is a distinct primary image; `images` is the gallery. Media paths must be relative, remain inside the package, exist when the mod is installed, and use PNG, JPEG, WebP, GIF, or SVG. A conventional package layout is:

```text
<package-id>/
  mod.json
  dlls/...
  media/
    icon.webp
    screenshot-1.webp
```

## Language IDs

The manifest uses stable BCP 47-style IDs rather than localized display names:

| ID | Game language |
|---|---|
| `en` | English |
| `fr` | French |
| `de` | German |
| `it` | Italian |
| `ja` | Japanese |
| `ko` | Korean |
| `pl` | Polish |
| `pt-BR` | Portuguese (Brazil) |
| `ru` | Russian |
| `zh-Hans` | Simplified Chinese |
| `es-419` | Spanish (Latin America) |
| `es-ES` | Spanish (Spain) |
| `zh-Hant` | Traditional Chinese |
| `uk` | Ukrainian |

Unknown or differently cased IDs are rejected to keep catalog and localization behavior deterministic.

## Managed authoring

Place `mod.json` beside the managed `.csproj`. `PackageRogueMod` copies it verbatim and includes files under the adjacent `media/` directory. If no manifest is present, the target still generates the legacy minimal manifest from `RogueModModId`, `RogueModModName`, and `RogueModEntryPoint`.
