# Steam Workshop workspace

This folder is the workspace for Mega Crit's [sts2-mod-uploader](https://github.com/megacrit/sts2-mod-uploader) (`ModUploader.exe`).

| File | Status |
|---|---|
| `workshop.json` | Title, BBCode description, change note, tag `Tools & APIs`. `visibility` starts as `private`. |
| `content/` | Not tracked. `./build.ps1 -Workshop` fills it with the release files (DLL, `SpireLink.json`, LICENSE, README, `api.md`). |
| `image.png` | Cover image, required by the uploader. Must be a PNG under 1 MB; `./build.ps1 -Workshop` fails when it is missing or too large. Do not use Mega Crit's logo or official art. |
| `previews/` | Optional. Each image under 1 MB. If the folder is absent, existing previews on Steam stay unchanged. |
| `mod_id.txt` | Written by the first upload. Commit it, so later uploads update the same Workshop item. |

## Upload

```powershell
./build.ps1 -Workshop
ModUploader.exe upload -w path\to\spire-link\workshop
```

Before each upload, update `changeNote` to match the CHANGELOG entry and check that the version in `content/SpireLink.json` is the one you mean to publish. Set the supported game branches on the Workshop web page rather than in `workshop.json`; the uploader's README notes that `minBranch` / `maxBranch` behave inconsistently.

## Going public

`workshop.json` keeps `visibility` at `private`, so the first upload creates a Workshop item only you can see. Use that for the preview: subscribe with your own account, start the game, check that the mod loads from the Workshop folder (the game asks to allow mods), that `GET /health` answers and that the Workshop page text reads well. Then make it public in one of two ways: change `visibility` to `public` in `workshop.json` and upload again, or switch the item to Public on its Steam Workshop page (Owner Controls, Change Visibility). Nothing is public until you do one of these.
