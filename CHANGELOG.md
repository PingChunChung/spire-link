# Changelog

Versions follow SemVer and stay at 0.x while the game is in Early Access. A release that only restores compatibility with a game update bumps the patch number; a change to the API bumps the minor number.

## 0.3.0

First public release. Verified only on game v0.107.1, singleplayer only.

- Local HTTP API on `127.0.0.1` (default port 15528) with Bearer key authentication: observe the state and the available actions, submit session- and version-guarded actions, look up receipts, diagnostics and reconcile-only recovery, card and keyword knowledge, local screenshots, journal events and metrics. See [docs/api.md](docs/api.md).
- Covers combat, map, events, rest sites, shops, rewards, card and relic selection, treasure, the main menu, character select, the timeline and game over.
- Maintainer's verification record: a complete Ascension 10 win recorded through this API (1110 actions, through both final bosses) was replayed in the real game on v0.107.1 with the maintainer's replay tool; every action executed as recorded, and the final floor and HP matched the original run. The recording and the tool are not part of the repository.
- Unfinished actions (`accepted`, `pending`) answer HTTP 202 on `/v1/actions` and on `/v1/requests/{id}`; receipts are kept until resolved and only resolved ones are evicted when the store is full (see docs/api.md).
- Deselecting a selected card on a card grid settles like selecting one, so select, deselect and confirm can follow each other.
- Event options carry `text_key` and events carry `dynamic_vars`, so costs and repeated options can be read in any game language.
- Popup yes/no buttons carry a `role`; in a language the mod does not know, the action uses the role as its option id.
- `non_interactive` modes share the same safety exclusions (merchant room, transform selection); an unsupported value falls back to `off`.
- `data_directory` inside `mods/`, the mod folder or a Workshop content folder is rejected, also when the path only reaches such a folder through a junction or symbolic link; the journal queue is bounded, drops are counted and logged as a rate-limited summary off the game thread; `max_sessions` prunes old session folders in the background, the current session counted in the quota.
- `/health` no longer returns error text: `last_error` and `evidence_error` are `null` or a fixed code, with `has_last_error` / `has_evidence_error` beside them; the Release DLL carries no build-machine path.
- Slippery Bridge: from the 8th Hold On on, the option keeps the same localization key; the completion check accepts that and still requires the cost and the HP loss to advance.
- Settings and data live in `%LOCALAPPDATA%\SpireLink`, so Workshop updates keep the API key.
- Automatic screenshots (`auto_capture`) are off by default.
- `GET /health` reports the game version and `"compatibility": "untested_game_version"` on game versions this release was not verified on, if the mod starts there at all. The manifest declares `min_game_version` 0.107.1.
- Two Harmony patches: one fixes a card grid that throws every frame when scrolled at certain card counts, the other skips, in fast mode, an event animation that blocks the main thread.
