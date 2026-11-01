# Spire Link

Spire Link is a local mod for Slay the Spire 2. It runs an HTTP API inside the game that listens only on `127.0.0.1`, so external programs can read the current game state and send actions such as playing a card, choosing a path, claiming rewards or driving menus. The mod makes no decisions itself; use it to connect your own helper tools, run logging or an AI.

The current version is 0.3.0, verified only on game v0.107.1, singleplayer only. On another game version the mod may fail to load or to read some screens; if it starts, `GET /health` reports `"compatibility": "untested_game_version"`. The maintainer's verification record for this version: a complete Ascension 10 win recorded through this API (1110 actions, through both final bosses) was replayed in the real game on v0.107.1 with the maintainer's replay tool, every action executed as recorded, and the final floor and HP matched the original run. The recording and the tool are not part of this repository.

## Install (players)

Install it one way only: subscribe on the Steam Workshop, or download `SpireLink-<version>.zip` from Releases and put the `SpireLink` folder inside it into the game's `mods` folder, so that you end up with `Slay the Spire 2/mods/SpireLink/SpireLink.dll`. Start the game and allow mods to load when it asks.

- A modded game uses a separate set of save files; your unmodded saves are not affected, and they do not show up in the modded saves.
- Do not install it both from the Workshop and in `mods/`; with the same mod in both places the game loads only one, and which one depends on the game version.
- To turn it off for a while, disable it in the Modding tab of the game's settings or start the game with `-nomods`. To remove it, unsubscribe or delete `mods/SpireLink`, then delete `%LOCALAPPDATA%\SpireLink` if you also want its data gone.

The player readme shipped in the release zip, with these steps in full, is [packaging/README.md](packaging/README.md).

On first launch the mod creates `%LOCALAPPDATA%\SpireLink\SpireLink.cfg`. It is JSON and contains `port` (default 15528) and a randomly generated `api_key`. It lives outside the mod folder so that Workshop updates keep the key. It ends in `.cfg` rather than `.json` because the game reads every `.json` under `mods/` as a mod manifest.

## Local data and privacy

The mod writes only to `%LOCALAPPDATA%\SpireLink` and sends nothing over the network. With no program connected it still writes a small journal: that the mod started and stopped (with its version, port and the game version), whether its two Harmony patches were installed, when one of them triggers (with the id of the current event room), and when the game's main thread stops running frames for 10 seconds (with the room type and event id). It saves no game states and no screenshots unless a program is connected or `auto_capture` is on. Automatic screenshots (`auto_capture`) are off by default. What is written, how it grows and how to clear it, is in [docs/api.md](docs/api.md#local-data-and-automatic-capture); every setting is listed in [packaging/README.md](packaging/README.md#settings). Security notes, including what to do if the API key leaks, are in [SECURITY.md](SECURITY.md).

## API

`GET /health` needs no authentication; every other endpoint requires `Authorization: Bearer <api_key>`. The two endpoints you will use most are `GET /v1/observe`, which returns the state and the actions currently available, and `POST /v1/actions`, which submits one of those actions. Every observation carries a `session_id` and a `state_version`; an action must include the version it was decided on, and is rejected if the state has changed since. The full list of endpoints, fields and error semantics is in [docs/api.md](docs/api.md).

```bash
curl http://127.0.0.1:15528/health
curl -H "Authorization: Bearer <api_key>" "http://127.0.0.1:15528/v1/observe?detail=compact"
```

## Build and test

Requires the .NET 9 SDK and an installed copy of the game. The mod compiles against `sts2.dll`, `GodotSharp.dll` and `0Harmony.dll` from the game directory; these files are never put into the repo or the release package.

```powershell
$env:STS2_GAME_DIR = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
./build.ps1            # build and produce the release zip in dist/
./build.ps1 -Install   # also install the DLL and manifest into the game's mods/SpireLink/ (the game must be closed)
./build.ps1 -Workshop  # also fill workshop/content/ for the Steam Workshop uploader; fails without workshop/image.png (see workshop/README.md)
./test.ps1             # run every test project under tests/ in turn
```

The test projects are small console programs. They compile only the parts of `src/` that do not depend on the game, such as the protocol, receipts, the capture quota and the card grid navigation math.

## Source layout

`src/SpireLinkMod.cs` is the entry point: it reads the configuration, starts the HTTP server and schedules every request onto the Godot main thread. `src/Link/` is the body of state reading and action execution. `State/Screens/` has one reader per screen; `Actions/` is grouped by screen with one handler per action; `Menus/` handles the main menu, character select and similar screens; `Game/` holds the shared helpers for reading cards, powers, relics and so on. Readers and actions share the same indexing and clickability checks, so the options listed in an observation are exactly the options an action can execute.

The mod applies only two Harmony patches to the game: one fixes a card grid that throws an exception every frame when scrolled at certain card counts, and the other skips, in fast mode, an event animation that would block the main thread.

`build.ps1` stops if the version in `mod_manifest.json`, `SpireLink.csproj` and `SpireLinkMod.Version` differ. The Release build maps source paths to `/_/` and embeds the PDB, so the shipped DLL carries no path from the build machine. Release notes are in [CHANGELOG.md](CHANGELOG.md).

## License

MIT, see [LICENSE](LICENSE).
