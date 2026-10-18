# Results submission

ACR Live Timing can send the results it observed to a **third-party server**, for
example a league's or a championship's. This document is the contract from the
client's side. Any server that implements it can receive results; ACR Live Timing
has no server built in, ships no server address and makes no request until the
operator pastes a key.

This is version 1 of the protocol: the schema versions below (`acrlt1:`,
`acr-pairing-key/1.0`, `acr-pairing/1.0`, `acr-pairing-response/1.0`,
`acrlt-results/1.0`) carry it. Requirement keywords (MUST, SHOULD, MAY) are used
in their usual sense.

*Version française : [results-submission.fr.md](results-submission.fr.md).*

## Principles

- **Generic.** The client knows no server and applies no server's rules. A server
  returns nothing but its event (`eventId`, `eventName`), an optional display name
  (`serverName`) and optional event settings that the operator can change.
- **Raw facts.** The client sends each driver's status, time, penalty, sectors and
  car per stage. No position, total, points or disqualification: the server
  validates and classifies.
- **Manual.** Nothing is sent until the operator clicks *Send* after a preview.
  There is no automatic upload and no automatic retry: if a send fails, the
  operator sends again.
- **Complete snapshot.** Every send carries the whole selection. A correction is
  simply a new send; the server keeps the latest.

```
server                                 host (ACR Live Timing)
──────                                 ──────────────────────
hands out a pairing key   ──────────►  pastes the key, checks the host, clicks Pair
exchange endpoint         ◄──────────  POST /v1/ingest/pairings/exchange
                          ──────────►  token + submission URL + event
                                       … rally …
submission endpoint       ◄──────────  Send: POST {submissionUrl} (full snapshot)
                          ──────────►  202 stored, association: active|ended
                                       answer shown on the association
```

## 1. Pairing key

```text
acrlt1:<base64url, no padding, of a UTF-8 JSON document>
```

```json
{
  "schemaVersion": "acr-pairing-key/1.0",
  "serverUrl": "https://results.example.org",
  "pairingCode": "7K9M-4QPX"
}
```

| Field | Rule |
|---|---|
| `schemaVersion` | `acr-pairing-key/1.<minor>`. |
| `serverUrl` | Absolute `https` URL. A path, an IP address and a port are allowed (`https://203.0.113.10:8443/acr`); user info, query, fragment and a trailing `/` are not. The TLS certificate must be valid for the host. |
| `pairingCode` | Printable ASCII, 1 to 128 characters. Its format and lifetime are the server's business. |

- Unknown fields are ignored. The whole key is limited to 4 KiB. An invalid key is
  refused with the reason (prefix, base64url, JSON, version or URL).
- **Debug builds only** also accept plain `http` to a test server on the same PC
  or the local network: `localhost`, or a literal private IP address (`127.0.0.0/8`,
  `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `::1`, `fc00::/7`), with any
  port, e.g. `http://192.168.1.20:8080`. Never a host name other than `localhost`.
  Release builds accept `https` only and always validate certificates.
- The key is not encrypted or signed, and contains the code: it is a temporary
  secret.
- **The host is shown before any request**: `serverUrl`'s host in punycode
  (`IdnHost`), with the port when not 443. The operator checks it before clicking
  *Pair*; it is the only trust anchor.

## 2. Exchange

```http
POST {serverUrl}/v1/ingest/pairings/exchange
Content-Type: application/json; charset=utf-8

{
  "schemaVersion": "acr-pairing/1.0",
  "pairingCode": "7K9M-4QPX",
  "clientInstanceId": "0199e3b2-6d1f-7c2a-9e41-5b8f0c3d2a17",
  "appVersion": "1.4.0"
}
```

`clientInstanceId` is a random UUID generated once per installation, for support
and audit; it proves nothing.

Success is any `2xx` with:

```json
{
  "schemaVersion": "acr-pairing-response/1.0",
  "submissionToken": "<opaque>",
  "submissionUrl": "https://results.example.org/v1/ingest/rally-results",
  "eventId": "summer-2026-r03",
  "eventName": "Summer Rally 2026 — Round 3",
  "serverName": "Example League",
  "expiresAt": "2026-08-16T18:00:00Z",
  "authorizationId": "01K2D80A7VBZJ2R9X4Z8DF6M3P",
  "eventSettings": { "penaltyCapPct": 50, "dnfNoRejoin": true }
}
```

| Field | Required | Rule |
|---|---|---|
| `schemaVersion` | no | `acr-pairing-response/1.<minor>`. |
| `submissionToken` | yes | Non-empty opaque string. |
| `submissionUrl` | yes | `https`, **same origin** (scheme, host, port) as `serverUrl`. |
| `eventId` | yes | **String**, 1 to 128 characters. Sent back unchanged. |
| `eventName` | no | Display only. |
| `serverName` | no | Display only: a readable server name, useful for an IP address. |
| `expiresAt` | no | ISO 8601 UTC. The token is deleted once it has passed. |
| `authorizationId` | no | Non-secret id, shown for support. |
| `eventSettings` | no | Settings the server suggests for this event (below). |

- Unknown fields are ignored. `event.eventId` / `event.name` are accepted in place
  of `eventId` / `eventName`.
- A response breaking a rule above is a failed exchange; nothing is stored.
- `eventName` and `serverName` are plain text, control characters removed, shown
  truncated with "…" past 200 characters (never a failure). In the app,
  `serverName` is always shown **next to** the host, never instead of it:
  `Example League · 203.0.113.10:8443 — Summer Rally 2026 — Round 3`.

**Event settings.** `eventSettings` gathers what the server suggests for its
event, so the board, the page and Discord follow its rules. v1 defines:

| Field | Rule |
|---|---|
| `penaltyCapPct` | Number, 0 to 500: the *Penalty cap* percentage. |
| `dnfNoRejoin` | Boolean: the *DNF – No Rejoin* option. |

- Each field is optional; an invalid one is ignored and logged, never a failed
  exchange. Unknown fields are ignored: later minor versions may add some (start
  time, car…), always as suggestions shown to the operator, never as a check that
  blocks a send.
- On a successful exchange, the present values are applied to the Config panel and
  logged (*"Penalty cap set to 50 % by Example League."*). Reusing a stored
  association applies nothing.
- The operator can change them at any time. While a setting differs from the one
  an active association suggested, a small warning sits next to it, naming that
  association in its tooltip.
- They are stored with the association. They change only the board; the payload
  carries raw facts either way.

| Exchange result | Client |
|---|---|
| `2xx` valid | association created (§ 3) |
| `400`, `401`, `403`, `404`, `409`, `410`, `422` | *"Key already used or expired — ask for a new one."* |
| `429` | *"Too many attempts — try again later"*, with the `Retry-After` delay |
| network error, timeout, `5xx` | *"Server unreachable — try again"*, key kept |
| `3xx` | error: redirects are never followed, here or on submission |

**Pasting a key again.** Keys are recognised by `serverUrl` + `pairingCode`. A key
already exchanged reuses its association without a request, unless the server
refused its token (`401`/`403`): the code is then exchanged again and the server
decides. A failed exchange never removes an association that still has a token
(*"Existing association kept."*).

**Storage.** The token is encrypted with DPAPI (`CurrentUser`, association id as
entropy) in `ACRLiveTiming.pairings`, next to the settings file, with the rest of
the association, and survives restarts. It never appears in the settings file, the
log, an error message, the UI or the web page. The pairing code is not stored, only
a SHA-256 of `serverUrl` + `pairingCode`.

## 3. Associations

An association is one successful exchange: server origin, `eventId`, token, and
the instant the exchange answered (for `startedAfterPairing`, § 5).

The server decides the state; the operator only chooses whether *Send…* goes to
an association:

| Shown | State | Sent to | On the web page |
|---|---|---|---|
| ▶ | `active`, not paused | yes | yes |
| ⏸ | `active`, paused by the operator | no | no |
| ⏹ | `ended`: the server answered `ended`. The token still serves corrections until the app closes: clicking ⏹ sends to it again (▶). | no, unless clicked | no |
| ✕ | `refused` or `expired`: token deleted; listed until the app closes | never | no |

A new association starts as ▶. Clicking ▶ pauses it, clicking ⏸ resumes it.

**Server signal.** The server never contacts the client; it acts through its
responses. An acknowledgement may carry `"association": "active"` or `"ended"`.
**Only `"ended"` ends the association; absent, unknown or unreadable means
`active`.** `active` puts an ended association back to `active`. A revoked token
shows up as `401`/`403` on the next send.

**Token deletion**, at the first of:

1. `401`/`403` on a send (`refused`);
2. `expiresAt` passed (`expired`);
3. app closed while the association is `ended`;
4. *Remove* by the operator: the association leaves the list at once.

Only `active` associations are kept across restarts.

**Last send.** Each association row shows the answer to its last send until the
app closes, e.g. *21:42 — Summer Rally — Stored: 14 drivers, 6 stages.* (§ 6).

**Web page.** The page and `/state` show the events results are sent to — the
associations shown ▶ that are `active` — as `serverName — eventName`, with the
host (`IdnHost`, never the port) in place of an empty `serverName`, flagged
*🏆 Counts for*. Never the URL, the token or the key.

**Group names.** A group created in the Stages panel while an association is active
is named after the first active association's `eventName` instead of `Rally N`.

## 4. Operator interface

A **Servers** panel, next to the Discord one.

- **Associations**: one row each — the state button (▶ ⏸ ⏹ ✕, § 3), then
  `serverName · host — eventName`, its state and last send (the server's message,
  in full in a tooltip), and a delete button (*Remove*, which asks first: sending
  to the event again needs a new key).
- **Pairing key**: the key field (its placeholder says so); the decoded host (or
  what is wrong with the key) shows under it as soon as it is pasted, and *Pair*
  makes the exchange.
- **Send**: a selector with *All stages* and each rally group (as for Discord) and
  one button: **Send…** when at least one association is ▶, **Save…** (JSON
  file, § 7) when none is. Both open the preview first. A folder button opens the
  `Results` folder.

Every send and its answer are also written to the activity log (status and
message, never the token or the payload). UI strings are English, like the rest of
the app.

## 5. Payload

```http
POST {submissionUrl}
Content-Type: application/json; charset=utf-8
Authorization: Bearer <submissionToken>
User-Agent: ACRLiveTiming/<appVersion>
```

```json
{
  "schemaVersion": "acrlt-results/1.0",
  "eventId": "summer-2026-r03",
  "runId": "5f0c2a9e-8d3b-4e71-a6c4-2b9d71e0f3a8",
  "generatedAt": "2026-08-15T20:42:12.418Z",
  "submissionMode": "operator-confirmed",
  "appVersion": "1.4.0",
  "group": 1,
  "groupName": "Summer Rally",
  "stages": [
    { "order": 1, "routeId": "AlsaceS4SaverneFullForward", "name": "Saverne",
      "lengthKm": 9.2, "startedAfterPairing": true,
      "startedAt": "2026-08-15T20:05:31.204Z",
      "inGameStartTime": "17:40", "weather": "Light rain · 12°C" },
    { "order": 2, "routeId": "WelesS3HafrenNorthFullForward", "name": "Hafren North",
      "startedAfterPairing": true, "startedAt": "2026-08-15T20:21:09.877Z" }
  ],
  "participants": [
    {
      "steamId64": "76561190000000001",
      "eosPuid": "0002aaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "usernameAtRun": "Driver One",
      "nation": "France",
      "nationName": "France",
      "stages": [
        { "order": 1, "status": "finished", "timeMs": 462871, "penaltyMs": 10000,
          "sectorsMs": [151034, 151078, 150759],
          "carId": "SkodaFabiaRSRally2", "carName": "Škoda Fabia RS Rally2" },
        { "order": 2, "status": "dnf", "timeMs": null, "sectorsMs": [98410],
          "carId": "SkodaFabiaRSRally2", "carName": "Škoda Fabia RS Rally2" }
      ]
    },
    {
      "steamId64": null,
      "eosPuid": null,
      "usernameAtRun": "Driver Two",
      "nation": "SwitzerlAnd",
      "nationName": "Switzerland",
      "stages": [
        { "order": 1, "status": "finished", "timeMs": 460002, "penaltyMs": 0,
          "sectorsMs": [153300, 153400, 153302],
          "carId": "HyundaiI20NRally2", "carName": "Hyundai i20 Rally2" },
        { "order": 2, "status": "not_started", "timeMs": null }
      ]
    }
  ]
}
```

A payload carries **one selection**: a rally group, or *All stages*.

### Envelope

| Field | Rule |
|---|---|
| `schemaVersion` | `acrlt-results/1.0`. |
| `eventId` | From the exchange, unchanged. |
| `runId` | Random UUID of the selection, the same for every send and every destination: created with the group, or for *All stages* when the session starts or is reset. |
| `generatedAt` | UTC, ISO 8601, milliseconds. |
| `submissionMode` | `operator-confirmed`. |
| `appVersion` | ACR Live Timing version. |
| `group` | Host group id, `0` for *All stages*. Session-local and reused: match on `runId`. |
| `groupName` | Host rally name. Absent for *All stages*. |

### `stages[]`

The selection's stages that have data, in run order: every stage of the rally group, ticked or not, or for *All stages* the stages ticked on the board.

| Field | Rule |
|---|---|
| `order` | 1, 2, 3… Participant entries refer to it. |
| `routeId` | Route **as the game sends it on the network** (`WelesS3HafrenNorthFullForward`), as in the game's save files. Never the catalog's normalised id. `null` when never received. |
| `name`, `lengthKm` | From the content catalog, for display. `name` falls back to the raw id; `lengthKm` absent when unknown. |
| `startedAfterPairing` | `true` when the stage started after **this destination's** association was made. |
| `startedAt` | UTC start, from the Windows clock. Absent when the start was not seen. |
| `inGameStartTime` | In-game time of day at the start, `HH:mm`, when received. |
| `weather` | Forecast label as on the page (`Light rain · 12°C`), when received. |

`startedAfterPairing`: a stage starts when the client sees its run start (the
start sequence that opens its column on the board; a stage joined while it was
already being raced is never on the board). Stage starts and associations are
ordered with a monotonic clock; an association from an earlier run of the app
precedes every stage of this run. The value is computed per destination when the
payload is built, never stored with the stage. A stage whose start was not seen is
`false`.

### `participants[]`

Drivers with an entry on at least one of the payload's stages, in no order.

| Field | Rule |
|---|---|
| `steamId64` | Decimal string, or `null` if the identity was never received. |
| `eosPuid` | EOS product user id, or `null` likewise. |
| `usernameAtRun` | Display name, for audit only — never for matching. |
| `nation` | Token as the game sends it (`France`), or `null` if unknown. Sent even with *Hide nationalities* on: that option only hides them on the board. |
| `nationName` | Its name from the content catalog, the token when unknown, `null` with `nation`. For display only. |
| `stages` | One entry per top-level stage, same `order`. |

A SteamID64 or EOS PUID appears at most once per payload.

| `status` | Meaning | Fields |
|---|---|---|
| `finished` | Finish confirmed by the driver's car or the game's session result. | `timeMs`, `penaltyMs`, `sectorsMs`, `carId`, `carName` |
| `dnf` | Retired, disqualified or left (the game does not tell them apart reliably). | `timeMs: null`, `sectorsMs` of the sectors passed (may be empty), `carId`, `carName` |
| `not_started` | No entry on this stage. | `timeMs: null` |

- `sectorsMs`: each sector's time in whole milliseconds, penalty-free. They are the
  differences between the game's cumulative times rounded to the millisecond, so
  they add up exactly to the stage time; one may differ by 1 ms from the sector
  time the game shows.
- `penaltyMs`: the penalty the game applied, in milliseconds.
- `timeMs` = sum of `sectorsMs` + `penaltyMs`, exactly: the time shown on the
  board.
- `carId`: car **as the game sends it** (`SkodaFabiaRSRally2`), or `null`.
  `carName`: its name from the content catalog, the raw id when unknown; for
  display only.
- **Casing varies.** Car, route and nation names are registered on each player's
  client: `HyundaiI20NRally2` and `Hyundaii20NRally2` were both seen. The client
  sends what it received; servers compare without case.

Never sent: positions, totals, gaps, points, the penalty cap, the *No rejoin*
option, DSQ, a revision number.

## 6. Sending

### Preview

*Send…* and *Save…* first open a **preview**: a confirmation window that lists
the selection's stages and drivers, the destinations, the warnings and anything
that blocks, with *Confirm* and *Cancel*. Nothing is sent or written before
*Confirm*.

The preview refuses, with the reason, while:

- one of the selection's stages is running (`Racing` or `Finishing`);
- a cell is **inconclusive** (driver still on the stage, unconfirmed finish, split
  without outcome) — each case is listed; wait for the session result, or leave
  the stage out (untick it for *All stages*, ungroup it for a rally);
- no stage or no participant is left.

Warnings, never blocking: drivers without identity, stages without `routeId`,
entries without `carId`, stages with `startedAfterPairing: false` (per
destination). The preview ends with *"N drivers: their SteamID/EOS IDs will be
sent to ‹host›."* per destination.

### Destinations

One confirmation builds **one payload per association shown ▶** and sends them at
once. They share `runId`, `group`, `groupName`, `stages` and `participants`; each
has its own `eventId`, `startedAfterPairing` and answer. One destination failing
does not affect the others.

- One request per send, with a 30 s timeout. An association cannot be sent to
  again until its request has answered or timed out.
- Sending again, corrected or unchanged, is simply a new send. The server keeps
  the latest per `eventId` + `runId`; `generatedAt` tells the sends apart.
- Every payload sent is saved as a JSON file (§ 7). Nothing is resent
  automatically, even after a restart.

### Responses

| HTTP | Last send shows |
|---|---|
| `2xx` | *Stored — ‹server's message›*, or *Stored*; `association` applied (§ 3) |
| network error, timeout | *Server unreachable — send again.* |
| `429` | *Too many requests*, with the `Retry-After` delay |
| `401`, `403` | *Association refused — a new key is needed.*; token deleted |
| any other | *Refused (HTTP n)*, with the server's message |

Both kinds of answer may carry a JSON body:

```json
{ "association": "active", "message": "Stored: 14 drivers, 6 stages." }
```

```json
{ "error": "event_closed", "message": "This event no longer accepts results." }
```

The client shows `message`, else `error`, else the start of a non-JSON body: plain
text, control characters removed, 300 characters at most. An acknowledgement means
"stored", not "validated".

## 7. JSON files

Results are always available as files, with or without a server.

- **Copy of every send.** Each payload sent is also written, byte for byte, to the
  `Results` folder next to the settings file, whatever the answer:
  `yyyyMMdd-HHmmss_<host>_<eventId>.json` (characters invalid in a file name
  replaced by `_`). The panel has a button to open the folder. Files are never
  deleted by the app. A file that cannot be written does not stop the send; the
  log says so.
- **Save…** With no association shown ▶, the button is **Save…**: it writes the
  selection to a file the operator chooses, without any request. The document is
  the same `acrlt-results/1.0` payload without the destination fields (`eventId`,
  `startedAfterPairing`), with `submissionMode: "file-export"`. Same preview checks
  as *Send*.

These files hold SteamIDs and EOS IDs in clear; the preview says so.

## 8. Security and privacy

- TLS with certificate validation, always, except the Debug-only `http` test
  addresses of § 1; no redirects.
- The token goes only in `Authorization`, only to `submissionUrl`.
- SteamID64 and EOS PUID leave the machine only in a payload the operator confirmed
  for a server whose key they pasted. Locally, they are written only to the JSON
  files of § 7. They are never on the web page, in `/state`, in copy/CSV/PNG
  exports, on the public tunnel or in Discord messages.
- No request before a key is pasted and *Pair* clicked; then only the exchange and
  submissions.
- The personal data a server receives (pseudonyms, nations, SteamID64, EOS PUID)
  are its operator's responsibility from then on: their storage, use, publication
  and deletion follow that server's own rules and privacy policy. The authors of
  ACR Live Timing receive none of it.

## 9. Server checklist

1. Pairing codes bound to one event, short-lived, with limited attempts.
2. HTTPS with a certificate valid for the key's host.
3. A high-entropy token scoped to the event; store its hash only.
4. `submissionUrl` on the same origin; `eventId` a string of ≤ 128 characters.
5. `2xx` only once the payload is stored durably.
6. `association: "ended"` in the acknowledgement that closes the event
   (`"active"`, or nothing, keeps it open), and a short `message` for the
   operator in every answer.
7. `400` for an unknown `schemaVersion`.
8. Match drivers on `steamId64` / `eosPuid`, never `usernameAtRun`.
9. Treat `routeId`, `carId`, `nation` as raw game names: compare without case,
   keep unknown ones.
10. Keep the latest payload per `eventId` + `runId` (latest `generatedAt`). The
    client may send the same content again, after a timeout or on the operator's
    request; such a send is not an error.
11. Optionally: use `runId` to spot one rally sent twice, and
    `startedAfterPairing` / `startedAt` to refuse a late pairing.
12. Optionally: return `eventSettings` so the host's board follows the event's
    rules (penalty cap, DNF – No Rejoin).
