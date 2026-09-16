# No-Delete Rules

## Rules

1. **No `Remove-Item`, `rm`, `del`, or equivalent on any path outside `bin/` and `obj/`.**
2. **Never delete a folder that may contain runtime data.** Data lives in `bin\Debug\net8.0-windows\data\`, `music\`, `config\`, and any `.db` / `.db-shm` / `.db-wal` file.
3. **Before any build that cleans output, back up data first** (see `.github/rules/backup-data.ps1`).
4. **`Remove-Item` is permanent.** It does not go to the recycle bin. Treat it as irreversible.
5. **If a task requires removing data, stop and ask.** Do not proceed.
6. **Prefer copy/overwrite over delete.** Move data to a safe path, do the work, restore it.

## Applies to

All interactions in this workspace.