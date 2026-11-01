# Spire Link

Spire Link is a local mod for Slay the Spire 2. It runs an HTTP API on your machine so external programs can read the current game state (hand, enemy intents, map, shop, rewards and more) and send actions such as playing a card, choosing a path or claiming a reward. Use it for your own helper tools, for run logging, or for AI research.

The mod makes no decisions for you. With nothing connected, you play the game as usual.

Tested on game v0.107.1, singleplayer only.

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

## Local data

The mod writes only to `%LOCALAPPDATA%\SpireLink`:

- `SpireLink.cfg`: the settings above.
- `sessions/`: a small journal per game launch. With no program connected it only records that the mod started and stopped. While a program is connected it records the requests it sends.
- Screenshots, only when a connected program asks for one, or when you set `auto_capture` to `true` in `SpireLink.cfg`. Automatic capture is off by default; when on, it saves a few screenshots per run (run start, elites, bosses, combat end, critical HP), at most `max_captures` (default 100) per run.

Nothing is sent over the network. To clear the data, close the game and delete the `sessions` folder.

## Security

- The API listens on `127.0.0.1` only, so other machines cannot connect. Do not forward or proxy the port to other machines.
- `GET /health` needs no key; every other endpoint requires `Authorization: Bearer <api_key>`.
- Any program on your computer that can read your `SpireLink.cfg` can control the game. Only run programs you trust.
- If the key leaks, close the game, delete `SpireLink.cfg` and start the game again; a new key is generated.

Endpoints and fields are described in `api.md`.

## Known limits

The mod depends on the game's internal screen structure and class names, so a game update may require a mod update before it reads or acts correctly. On a game version it was not tested on, the mod still loads and `GET /health` reports `"compatibility": "untested_game_version"`. Singleplayer only.

## License

MIT, see `LICENSE`.
