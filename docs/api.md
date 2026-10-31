# HTTP API

The default service address is `http://127.0.0.1:15528`. `GET /health` needs no authentication; every other endpoint requires `Authorization: Bearer <api_key>`. The token comes from `SpireLink.cfg` (JSON) next to the mod. Do not put it in prompts, command output or shared bundles.

The API returns UTF-8 JSON. `session_id` separates mod sessions; `state_version` and `state_hash` identify a state snapshot within that session. Every action taken on a state should keep the session and version it was observed with; never apply a new session/version to an old decision.

## Endpoints

| Method / path | Description |
|---|---|
| `GET /health` | Mod liveness, version, build hash, session and receipt capacity. |
| `GET /v1/observe` | State, action candidates, control/pending metadata. |
| `POST /v1/actions` | Submit one session/version-guarded mutation. |
| `GET /v1/requests/{request_id}` | Look up the receipt of an earlier request; creates and resends nothing. |
| `GET /v1/diagnostics` | Readiness, pending request, errors, receipt capacity and reconcile guidance. |
| `GET /v1/instance` | Actual PID, executable, Godot user data, account and save backend paths, for isolation checks. |
| `POST /v1/recovery` | Accepts only `mode: "reconcile"`; re-reads the current state. |
| `GET /v1/knowledge/cards` | Search, page through or look up the card catalog by ID. |
| `GET /v1/knowledge/keywords` | Descriptions of card keywords and power IDs. |
| `POST /v1/decisions` | Record a standalone decision; never captures a screenshot because of importance. |
| `POST /v1/captures` | Ask the mod to save the current viewport as a PNG on the local machine. |
| `GET /v1/metrics` | Route, timing and counter metrics. |
| `GET /v1/events?limit=100` | Recent journal events; `limit` is 1–500. |
| `GET /v1/capabilities` | Server version, limits, capabilities and unsupported items. |

## Observe and control versions

`GET /v1/observe?detail=compact|full&since_version=N&wait_ms=0..10000`. `wait_ms` is at most 10000 and lets the server wait briefly while the state is unchanged. A full envelope normally looks like this:

```json
{
  "api_version": "1",
  "session_id": "...",
  "state_version": 42,
  "state_hash": "...",
  "observed_at": "...",
  "ready": true,
  "state": {},
  "actions": [{"action_id": "...", "action": "...", "arguments": {}, "label": "..."}],
  "control_revision": 8,
  "pending_mutation": false,
  "interaction_status": "ready",
  "timing": {"server_processing_ms": 1.2}
}
```

If the version has not changed, the server may omit `state` and `actions` and return `unchanged: true`, while still returning session/version/hash, `control_revision`, and the pending/readiness/status control metadata. `unchanged` therefore only means the state version is the same; it does not mean the pending mutation or the interaction status is unchanged. When comparing observations, check at least `session_id`, `state_version`, `control_revision`, `pending_mutation`, `ready` and `interaction_status`. `compact` keeps the complete permanent `player.deck` and the deck summary but omits the heavier pile contents and some keywords; use `full` when you need pile contents or complete keywords.

In both compact and full state, `player.deck` is the complete permanent deck of the current run, with card data, upgrade level and a deck summary. It is not just the starting deck, and it is not derived from the temporary combat piles. In combat the draw pile does not expose the future draw order: `draw_pile_ordering` is `unordered_unknown_draw_order`, so do not infer draw probabilities or order from the sorted list. Hand action indices, card instance IDs and enemy combat epochs are references valid at that moment; observe again after playing a card or after a state transition.

## Actions, receipts and late completion

A `POST /v1/actions` body needs at least:

```json
{
  "request_id": "caller-generated-uuid",
  "expected_session_id": "session-id-from-the-decision-observation",
  "expected_version": 42,
  "action_id": "...",
  "wait_timeout_ms": 5000,
  "decision": {
    "decision_id": "...",
    "policy_id": "...",
    "reason": "...",
    "expected_effect": "...",
    "candidates": [],
    "decision_duration_ms": 18,
    "importance": {
      "level": "notable",
      "reason": "key choice before spending a scarce resource",
      "tags": ["resource"],
      "capture": true
    }
  }
}
```

`decision` is optional. If it carries `importance`, `level` must be `routine`, `notable` or `major`; `reason` is required and 1–500 characters long; `tags` holds at most 10 entries of at most 64 characters each; `capture`, if present, must be a boolean. With `capture: true`, the server records a highlight once the action has met its completion condition and saves a local PNG asynchronously. The image is never sent to the model automatically. By contrast, `POST /v1/decisions` only records a standalone decision and never captures a screenshot, even when it carries `importance`.

`wait_timeout_ms` is 0–15000. The server may answer `completed`, `accepted`, `pending`, `unknown` or `rejected`. An action still running when the wait times out returns HTTP 202; do not resend it with a new request ID. Query the same receipt with `GET /v1/requests/{id}`. Late game-state evidence can update an earlier `accepted`, `pending` or `unknown` receipt to `completed`, with `initial_status`, settlement metadata and an `observation_ref`. Replays do not keep the full observation, and the response is not guaranteed to be identical byte for byte; observe again to get the current state.

Receipt keys are session-scoped, and the fingerprint of the JSON body must match for the same ID. `receipt_capacity` defaults to 16384 and ranges from 1024 to 65536; nothing is evicted within a session. When the store is full, a new action returns HTTP 503 and is not executed. Reusing an ID with a different body returns 409 `request_id_conflict`. Version, session and action guard rejections return HTTP 409 (for example `stale_session`, `stale_state`, `action_unavailable`, `action_pending`); discard the old decision, observe again and decide again. The game runtime validates the action once more, so the action list is not a promise that execution will succeed.

## Diagnostics and reconcile-only recovery

`GET /v1/diagnostics` returns `status` (such as `ready`, `waiting`, `blocked`, `unsupported`, `ended`), a reason, the pending request's age and postcondition, receipt count and capacity, and a recovery recommendation. If an action has been dispatched, do not treat a timeout as failure. Query the same receipt and observe `pending_mutation`, `ready`, `control_revision` and the interaction status again. `POST /v1/recovery` accepts only `{ "expected_session_id": "...", "expected_version": 42, "request_id": "...", "mode": "reconcile" }`; it only re-reads the state and reports `reconciled`, `waiting` or `blocked`. It does not click, resend or force an unlock. If the state is still unclear, save the evidence and stop automated actions.

## Card and keyword/power knowledge

`GET /v1/knowledge/cards?offset=0&limit=50&query=...` returns paged summaries of every registered card; `limit` is 1–100 and `query` is at most 200 characters. Use `ids=ID1,ID2` (at most 100 IDs) to fetch detailed entries instead of transferring the whole catalog again. Card data includes the base variant and an upgraded preview when one can be built; for a few cards the upgraded preview may be unavailable, so check `upgraded_preview_available`. The content is the game's display text and numbers, not a full execution simulation of each card.

`GET /v1/knowledge/keywords?ids=...` looks up keyword IDs or power IDs; without `ids` it returns the complete registry. Both knowledge responses carry `catalog` metadata: game assembly/version/build, locale, `catalog_hash`, card and keyword counts, and catalog build timing. Caches should be keyed by catalog hash, locale and ID; when the metadata changes, re-fetch the entries you need. Normally hand the complete permanent deck and the options on screen to the policy layer, and query on demand only for an unfamiliar card, upgrade, keyword or power. Descriptions in the catalog are the text of that game build; they are not a simulation of every state modifier, trigger order or future card order.

`catalog.card_description_context="out_of_combat_template"` means the template fetches descriptions with `PileType.None`, including the derived cards mentioned in tooltips. Character-specific energy icons are unified to `[energy]` in the catalog, and `catalog.text_tokens` gives its meaning as `one_energy`; other resource icons such as stars are not treated as energy. The current hand keeps the runtime descriptions; this normalization applies only to the knowledge catalog.

## Decisions, captures, events and timing

`state.hand_select.selector` keeps the game's current `CardSelectorPrefs`: `prompt_table`, `prompt_key`, `min_select`, `max_select`, `require_manual_confirmation`, `cancelable`, plus the `filter_target_model_id` of the current filter delegate. These are runtime truth; they are null when the prefs or the prompt cannot be read, and the source is never guessed from localized text. The ID can also be null when the filter target is not a model, so do not treat this field as the source of every card selection. `selected_instances` is an array of instance ID strings; the complete selected cards are listed in `selected_cards`.

When `end_turn` reaches a recognized Well-Laid Plans retain selection, a completed parent means the dispatch reached a sub-flow that needs input; it does not yet mean the turn has ended. The AI must use the `combat_select_card` / `combat_confirm_selection` candidates from a fresh observation, wait for each receipt, and then verify the next turn. Buying the Gnarled Hammer and entering its enchant card selection uses the same dispatch-completed semantics. For an unknown source, nothing is force-unlocked and the parent action is not resent.

`POST /v1/decisions` requires `expected_session_id` and a positive integer `state_version`; the server validates the version and only writes to the journal. It never saves a screenshot. `POST /v1/captures` requires `reason`, at most 500 characters; `expected_version` is optional, and if a version is given the matching session guard must be given too. A capture writes a local PNG and records its hash, version, frame policy and `sent_to_model: false`. `evidence_persistence` on events and state means the write has been queued; it does not mean all data has been flushed.

`GET /v1/events` has a default `limit` of 100 and a maximum of 500. `timing.server_processing_ms` is the processing time measured by the server itself; the HTTP header `X-Bridge-Serialize-Ms` is the time spent serializing the response JSON, so clients can report the two separately. `decision_duration_ms` is measured by the caller; the mod does not assume it equals model thinking time. None of these times can stand in for another.

`max_captures` is a quota per live `context.run_id`, default 100; changing floor, act or room does not reset it. States without a run, such as the main menu, share a separate quota for the session. This is not a persistent cross-process save ID: restarting the game or reloading creates a new runtime run identity, and accumulation across restarts is not promised. `capture_scope` / `capture_run_id` in the response show which quota was charged; when the quota is exhausted the response is HTTP 429. Queued captures from an earlier run do not consume the new run's quota, and file names still use a sequence number that keeps increasing within the session.
