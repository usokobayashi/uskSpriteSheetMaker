# Localization

UI text lives here as one `.lang` file per language. Requirements, translation rules and the glossary are kept in the Obsidian vault:
`RootPROJECT/03_SpriteSheetMaker/多言語対応/` (read `00_入口.md` first).

- `ja.lang` is the base language and the source of truth. Add new keys here first, with a `# [kind] context` comment line above each key.
- `en.lang`, `zh-CN.lang`, `id.lang` hold translations as `key=value`. Missing keys fall back to Japanese.
- Format: UTF-8, one `key=value` per line, `#` starts a comment, `\n` is a newline, `\` is a backslash. Keep `{0}` placeholders unchanged.
- Files are embedded via `SpriteSheetMaker.csproj` (`EmbeddedResource`). Use `Loc.T("key")` or `BindText(control, "key")` in code; never hard-code UI text.
- Run `Tests` (`Loc_*`) after editing: duplicate keys, orphan keys and placeholder mismatches are checked.
