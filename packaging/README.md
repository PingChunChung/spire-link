# Spire Link

Spire Link is a local mod for Slay the Spire 2. It runs an HTTP API on your machine so external programs can read the current game state (hand, enemy intents, map, shop, rewards and more) and send actions such as playing a card, choosing a path or claiming a reward. Use it for your own helper tools, for run logging, or to let an AI play.

The mod makes no decisions for you. With nothing connected, you play the game as usual.

## Install

1. Close the game.
2. Put the whole `SpireLink` folder from the archive into the game's `mods` folder, so that you end up with `Slay the Spire 2/mods/SpireLink/SpireLink.dll`.
3. Start the game and allow mods to load. A modded game uses a separate set of save files; your unmodded saves are not affected.

To uninstall, delete the `mods/SpireLink` folder.

## Settings

On first launch the mod creates `SpireLink.cfg` (JSON) in the same folder. The fields you will usually care about are `port` (default 15528) and `api_key`. The `api_key` is generated randomly on first launch; do not share it.

The API binds to `127.0.0.1` only, so programs on other machines cannot connect. `GET /health` needs no authentication; every other endpoint requires `Authorization: Bearer <api_key>`. Endpoints and fields are described in `api.md`.

## Known limits

The mod depends on the game's internal screen structure and class names, so a game update may require a mod update before it reads or acts correctly. Singleplayer only.

## License

MIT, see `LICENSE`.
