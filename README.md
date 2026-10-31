# Spire Link

Spire Link is a local mod for Slay the Spire 2. It runs an HTTP API inside the game that listens only on `127.0.0.1`, so external programs can read the current game state and send actions such as playing a card, choosing a path, claiming rewards or driving menus. The mod makes no decisions itself; use it to connect your own helper tools, run logging or an AI.

The current version is 0.3.0, built against game v0.107.1, singleplayer only. This version was verified by replaying a complete Ascension 10 win (1110 steps, through both final bosses) in the real game: every step executed as recorded, and the final floor and HP matched the original run.

## Install (players)

Download `SpireLink-<version>.zip` from Releases and put the `SpireLink` folder inside it into the game's `mods` folder, so that you end up with `Slay the Spire 2/mods/SpireLink/SpireLink.dll`. Start the game and allow mods to load. A modded game uses a separate set of save files; your unmodded saves are not affected. The readme shipped in the release zip is [packaging/README.md](packaging/README.md).

On first launch the mod creates `SpireLink.cfg` next to the DLL. It is JSON and contains `port` (default 15528) and a randomly generated `api_key`. The file is not named `.json` because the game reads every `.json` under `mods/` as a mod manifest.

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
./build.ps1 -Install   # also install the DLL and manifest into the game's mods/ (the game must be closed)
./test.ps1             # run every test project under tests/ in turn
```

The test projects are small console programs. They compile only the parts of `src/` that do not depend on the game, such as the protocol, receipts, the capture quota and the card grid navigation math.

## Source layout

`src/SpireLinkMod.cs` is the entry point: it reads the configuration, starts the HTTP server and schedules every request onto the Godot main thread. `src/Link/` is the body of state reading and action execution. `State/Screens/` has one reader per screen; `Actions/` is grouped by screen with one handler per action; `Menus/` handles the main menu, character select and similar screens; `Game/` holds the shared helpers for reading cards, powers, relics and so on. Readers and actions share the same indexing and clickability checks, so the options listed in an observation are exactly the options an action can execute.

The mod applies only two Harmony patches to the game: one fixes a card grid that throws an exception every frame when scrolled at certain card counts, and the other skips, in fast mode, an event animation that would block the main thread.

## License

MIT, see [LICENSE](LICENSE).
