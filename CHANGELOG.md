# Changelog

Versions follow SemVer and stay at 0.x while the game is in Early Access. A release that only restores compatibility with a game update bumps the patch number; a change to the API bumps the minor number.

## 0.3.0

First public release. Tested on game v0.107.1, singleplayer only.

- Local HTTP API on `127.0.0.1` (default port 15528) with Bearer key authentication: observe the state and the available actions, submit session- and version-guarded actions, look up receipts, diagnostics and reconcile-only recovery, card and keyword knowledge, local screenshots, journal events and metrics. See [docs/api.md](docs/api.md).
- Covers combat, map, events, rest sites, shops, rewards, card and relic selection, treasure, the main menu, character select, the timeline and game over.
- Verified by replaying a complete Ascension 10 win (1110 steps, through both final bosses) in the real game: every step executed as recorded, and the final floor and HP matched the original run.
- Settings and data live in `%LOCALAPPDATA%\SpireLink`, so Workshop updates keep the API key.
- Automatic screenshots (`auto_capture`) are off by default.
- `GET /health` reports the game version and `"compatibility": "untested_game_version"` on game versions this release was not tested on. The manifest declares `min_game_version` 0.107.1.
- Two Harmony patches: one fixes a card grid that throws every frame when scrolled at certain card counts, the other skips, in fast mode, an event animation that blocks the main thread.
