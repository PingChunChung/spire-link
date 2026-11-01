# Spire Link

Spire Link is a local mod for Slay the Spire 2. It runs an HTTP API on your machine so external programs can read the current game state (hand, enemy intents, map, shop, rewards and more) and send actions such as playing a card, choosing a path or claiming a reward. Use it for your own helper tools, for run logging, or for AI research.

The mod makes no decisions for you. With nothing connected, you play the game as usual.

Verified only on game v0.107.1, singleplayer only. On another game version it may not load or may misread some screens; if it starts, `GET /health` reports `untested_game_version`.

## Install

Install it one way only, either from the Steam Workshop or by hand. If the same mod is in both places, the game picks one of them, and which one depends on the game version.

**Steam Workshop:** subscribe to Spire Link, then start the game. The first time the game finds a new mod it asks whether to load mods; choose to allow it.

**By hand:**

1. Close the game.
2. Put the whole `SpireLink` folder from the archive into the game's `mods` folder, so that you end up with `Slay the Spire 2/mods/SpireLink/SpireLink.dll`. Create the `mods` folder if it does not exist.
3. Start the game and allow mods to load.

**Separate saves:** once any mod is loaded, the game uses a separate set of save files for modded play. Your unmodded saves and progress are not touched, and they do not appear in the modded saves either.

## Turn off or remove

- **Turn off for now:** disable Spire Link in the Modding tab of the game's settings, or start the game with the `-nomods` launch option to load no mods at all.
- **Remove:** unsubscribe on the Workshop, or delete the `mods/SpireLink` folder. The mod's own data stays in `%LOCALAPPDATA%\SpireLink`; delete that folder too if you want it gone.

## Settings

On first launch the mod creates `%LOCALAPPDATA%\SpireLink\SpireLink.cfg` (JSON). The fields you will usually care about are `port` (default 15528) and `api_key`, which is generated randomly on first launch. The file lives outside the mod folder, so Workshop updates keep your key.

The file is read once, when the game starts; after editing it, restart the game. A value outside its range is clamped to the nearest limit; an unsupported `non_interactive` value is logged and treated as `off`.

| Field | Default | Range / values | Meaning |
|---|---|---|---|
| `port` | 15528 | 1024–65535 | TCP port of the API on 127.0.0.1. Outside the range the mod does not start. |
| `api_key` | random, 64 hex characters | at least 24 characters | Bearer token every endpoint except `/health` requires. |
| `data_directory` | `%LOCALAPPDATA%\SpireLink` | an absolute path | Where `sessions/` goes. A path inside the game's `mods` folder, inside the mod's own folder or inside a Steam Workshop content folder is rejected at startup (logged, the default is used), because the game reads every `.json` there as a mod manifest. |
| `auto_capture` | `false` | `true`/`false` | Sample the state continuously and save screenshots at run start, elites, bosses, combat end and critical HP. |
| `max_captures` | 100 | 0–1000 | Screenshots per run (and per session outside a run). |
| `max_sessions` | 0 | 0 or more | Session folders kept under `data_directory\sessions` in total, the current launch's folder included; shortly after startup the oldest beyond this number are deleted in the background (1 keeps only the current session). 0 keeps everything. |
| `observe_interval_ms` | 250 | 5–2000 | How often the state is sampled while a client waits for an action to settle or `auto_capture` is on. |
| `settle_stable_ms` | 80 | 0–500 | How long a changed state must stay unchanged before an action counts as settled. |
| `animation_mode` | `keep` | `keep`, `none`, `normal`, `fast`, `instant` | Pins the game's animation speed setting (the Fast Mode option) while the mod runs; `keep` leaves it alone. |
| `time_scale` | 1.0 | 0.5–8.0 | Engine time scale. Values other than 1.0 speed up or slow down the whole game. |
| `max_fps` | 0 | 0, or 5–240 | Frame rate cap; 0 keeps the engine default. |
| `low_processor` | `false` | `true`/`false` | Lets the engine sleep between frames when idle. |
| `non_interactive` | `off` | `off`, `combat`, `noshop`, `all` | Runs parts of the game on its fast path (waits, pauses and per-frame pacing skipped), meant for automated play. `combat` only in fights; `noshop` and `all` everywhere else too. Every mode leaves the transform card selection, the seconds after it and the merchant room at normal speed, because the fast path hangs the game on the transform animation and was once seen to crash it in a shop. |
| `trace_file` | empty | a file path | Appends one line per state sample and unhandled exception to that file, for crash forensics. |
| `receipt_capacity` | 16384 | 1024–65536 | How many action receipts are kept for `GET /v1/requests/{id}`. |

**The port is in use:** if another program (or a second copy of the game) already listens on the port, the mod logs `Initialize failed` with an `HttpListenerException` in the game's log, `GET /health` does not answer, and the game runs without the API. Set another `port` in `SpireLink.cfg` and restart the game. On Windows, `netstat -ano | findstr :15528` shows which process holds the port.

## Local data

The mod writes only to `%LOCALAPPDATA%\SpireLink`:

- `SpireLink.cfg`: the settings above.
- `sessions/`: a small journal per game launch. With no program connected it still writes a small journal: that the mod started and stopped (with its version, port and the game version), whether its two Harmony patches were installed, when one of them triggers (with the id of the current event room), and when the game's main thread stops running frames for 10 seconds (with the room type and event id). It saves no game states and no screenshots unless a program is connected or `auto_capture` is on. While a program is connected it also records every request, the game state before and after each action (one JSON file per state version under `states/`), and major run events, so a long automated session can take tens of megabytes; set `max_sessions` to have old session folders deleted shortly after startup.
- Screenshots, only when a connected program asks for one, or when you set `auto_capture` to `true` in `SpireLink.cfg`. Automatic capture is off by default; when on, it saves a few screenshots per run (run start, elites, bosses, combat end, critical HP), at most `max_captures` (default 100) per run.

Nothing is sent over the network. To clear the data, close the game and delete the `sessions` folder.

## Security

- The API listens on `127.0.0.1` only, so other machines cannot connect. Do not forward or proxy the port to other machines.
- `GET /health` needs no key; every other endpoint requires `Authorization: Bearer <api_key>`.
- Any program on your computer that can read your `SpireLink.cfg` can control the game. Only run programs you trust.
- If the key leaks, close the game, delete `SpireLink.cfg` and start the game again; a new key is generated.

Endpoints and fields are described in `api.md`.

## Known limits

The mod depends on the game's internal screen structure and class names, so a game update may require a mod update before it loads, reads or acts correctly. It was verified only on v0.107.1; on another version, if it starts, `GET /health` reports `"compatibility": "untested_game_version"`. Singleplayer only.

## License

MIT, see `LICENSE`.
