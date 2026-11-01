# Security

Spire Link lets programs on your computer read the game state and control the game. This page describes what it exposes and how to keep it local.

## What the mod exposes

- An HTTP server bound to `127.0.0.1` only (default port 15528). Other machines on your network cannot connect to it.
- `GET /health` needs no authentication and returns only status and version information.
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

Please open a GitHub issue that describes the problem without including a working exploit or your key, and ask for a private channel if the details are sensitive.
