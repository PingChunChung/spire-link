# Security

Spire Link lets programs on your computer read the game state and control the game. This page describes what it exposes and how to keep it local.

## What the mod exposes

- An HTTP server bound to `127.0.0.1` only (default port 15528). Other machines on your network cannot connect to it.
- `GET /health` needs no authentication. It returns the mod version and build hash, the API session id, the current state version, the receipt count and capacity, the game version and compatibility, and whether an error is recorded: `last_error` and `evidence_error` are `null` or a fixed code (`error_recorded`, `evidence_error_recorded`), with `has_last_error` and `has_evidence_error` as booleans. It never returns the error text (which can contain a local path), game state, the key or any file path; those are behind the key in `/v1/diagnostics` and `/v1/instance`.
- Every other endpoint requires `Authorization: Bearer <api_key>`. The key is 64 random hexadecimal characters generated on first launch and stored in `%LOCALAPPDATA%\SpireLink\SpireLink.cfg`. Keys are compared in constant time.
- The mod makes no outgoing network connections. Screenshots and the journal stay on your disk and are never sent anywhere by the mod.

## Keeping it local

- Do not forward, tunnel or proxy the port to other machines or the internet. Anyone who can reach the port and knows the key can play your game.
- Any program that runs as your user can read `SpireLink.cfg` and therefore control the game. Only run clients you trust.
- Do not paste the key into prompts, logs, screenshots, issues or shared bundles.

## If the key leaks

1. Close the game.
2. Delete `%LOCALAPPDATA%\SpireLink\SpireLink.cfg`.
3. Start the game. The mod writes a new config with a new random key; set `port` again if you had changed it.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting on this repository (Security tab, "Report a vulnerability"), which only the maintainer can read. If that form is not available to you, open a GitHub issue that describes the problem without a working exploit or your key, and ask for a private channel.
