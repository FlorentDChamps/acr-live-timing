# ACR Live Timing

[![build](https://github.com/FlorentDChamps/acr-live-timing/actions/workflows/build.yml/badge.svg)](https://github.com/FlorentDChamps/acr-live-timing/actions/workflows/build.yml)

**Live timing and shared leaderboard for Assetto Corsa Rally multiplayer lobbies.**

*Read this in [French / en français](README.fr.md).*

ACR shows stage results only inside the lobby, on each player's own screen. This app
passively decodes the game's network traffic on one player's PC and turns it into a
**live leaderboard web page** — every finisher's time (penalties included), nation
flag and car — that the whole lobby can follow in a browser, stage after stage, with
running session totals. Share it on the LAN or publish it to anyone with a one-click
Cloudflare tunnel link.

![The live leaderboard page during a stage](docs/screenshot.png)

<sub>Demo data — fictional pseudonyms, placeholder server address.</sub>

> ⚠️ **Passive analysis tool, for personal/educational use.** It only *reads* the
> traffic of lobbies you are playing in. It modifies nothing, injects nothing, and
> gives no driving advantage.

> **Unofficial fan-made project.** Not affiliated with, endorsed by, or associated
> with Kunos Simulazioni, Supernova Games Studios or 505 Games. *Assetto Corsa* is a
> trademark of its respective owners; the name is used here only to indicate
> compatibility. The tool reads **cleartext traffic only** and will never attempt to
> decrypt, defeat or work around any protection.

---

## Features

- **Zero configuration** — join an ACR lobby; the game server is auto-detected by
  protocol signature (IP and port change every lobby, nothing is hardcoded).
- **Live stage results** — driver names, split-validated final times (ms-exact vs
  the in-game screen, penalties included), stage name, nation flag and car model.
- **Session classification** — one column per stage run, running totals and ranks.
  Configurable *threshold rule*: a driver missing a stage — or slower than
  `fastest × (1 + threshold %)` — is counted at the threshold time and flagged.
  Any stage can be excluded from the totals with a checkbox. A stage you join already
  under way is **not** counted — its start was missed — but its name and the
  driver/car bindings are still learned, so the next, fully-captured stage is ready
  from the first split.
- **Finish-gated reveal** — a driver's time appears only once they actually cross
  the finish line, so intermediate splits never flicker or climb on the board.
  Detection is event-driven and exact: the game replicates a per-car race phase
  (`RaceStateData.Phase`); the transition to *Ended* marks the finish the instant it
  happens, with the frozen stage timer matching the displayed result to the
  millisecond. Streamed packet-by-packet, so replays reveal the same progression as
  live regardless of playback speed. A tool locking on mid-session re-identifies the
  timer stream from its wire shape, so no stage restart is needed; only genuinely
  timer-less captures (older recordings) fall back to split-completion gating.
  Toggleable in the Config panel — with gating off, the page shows each driver's
  latest cumulative split as it arrives.
- **DNF display** — a driver who posted split times but never crossed the line is
  shown as **DNF** on that stage (counted at the threshold time, like an absence).
  On the running stage it appears the moment the car's race phase turns
  *Retire/Disqualify*; on past stages it is inferred once the stage closed with the
  driver unfinished. A driver who quits before the first splits still shows as absent.
- **Live stage progression** — a horizontal track above the board shows every
  driver's live position along the current stage, auto-fitted between the leader and
  the last running car (max span adjustable in the Config panel). Each marker is
  named **at spawn, from the start line**: the car's owner PlayerState actor
  re-replicates its identity block (steamid + display name) every stage, and the
  decoder binds it to the car's telemetry component deterministically. A sector-split
  time-match remains as fallback (e.g. when the tool locks on mid-stage), so a car
  not yet identified shows as an anonymous dot until its first split. During the
  **first stage** after the tool locks on, data can therefore be partial: some
  markers may stay anonymous for a while and the board fills in as the first splits
  arrive. Everything is complete from the next stage on.
- **Lobby & stage header** — the page header shows the current lobby phase (Loading,
  Racing, Results, Service park…), the stage's **in-game start time** (time of day)
  and its **weather forecast**, all decoded from the replicated weather timeline and
  the lobby state machine.
- **Shareable web view** — embedded web server for the LAN, plus an optional public
  `https://….trycloudflare.com` link. The pinned `cloudflared` binary is downloaded
  once and **SHA-256-verified** against Cloudflare's published checksum before use.
  The Start / Publish buttons double as stop controls, and the public link has a
  one-click copy button.
- **Per-viewer display settings** — a gear button on the web page opens a panel that
  mirrors the host's ranking/display controls **locally**: exclude stages from the
  totals, change the penalty threshold %, resize the progression window, hide
  nationalities, toggle finish gating. Changing a stage/penalty/gating setting
  recomputes the board in the browser from the raw per-stage data the page already
  receives, so it never touches the host's own view or any other viewer. Settings are
  session-only (a reload reverts) and a **Reset to host config** button restores the
  host's published setup. Hidden in the OBS overlay modes. Saving the page with the
  browser's own **Save page as** bakes in the current standings, so the saved file
  opens offline (no server) with the settings panel still fully functional.
- **Custom page heading & link preview** — set an optional title and description
  (top-left panel) shown at the top of the shared page; URLs typed in either are
  clickable. Open Graph / Twitter Card metadata is emitted too, so pasting the link
  in Discord, Facebook, WhatsApp… shows that title and description as a rich preview.
- **Streaming overlays (OBS)** — two independent, resizable, transparent windows —
  one with the **general classification**, one with the **live progression bar** —
  that reuse the web page. Each is configured on its own in the *Overlays* panel:
  show/hide, *always-on-top*, click-through *lock*, and **background opacity** (in
  10 % steps). Resize to zoom them, then float them straight over the game as an
  on-screen overlay while you race. For OBS, capture them as **Browser Sources**
  instead — the *Overlays* panel has a *Copy OBS source URL* button per widget (see
  [Streaming with OBS](#streaming-with-obs)). (Start the local web server first —
  overlays load from it. A borderless / windowed-fullscreen game is required for a
  desktop overlay to sit on top; exclusive fullscreen can't be covered.)
- **Light or dark desktop UI** — the app picks up your Windows light/dark setting on
  launch, with a toggle in the header to switch at any time.
- **Record & replay** — optionally record the session to a standard `.pcap` file
  (Wireshark-compatible) and replay it later through the same decoding pipeline.
- **Remembers your setup** — all window state (options, page title/description, and
  each overlay's position, size, always-on-top, lock and opacity) is saved to an
  `ACRLiveTiming.config` file next to the exe and restored on the next launch.
- **No capture driver** — a Windows raw socket is used instead of Npcap/WinPcap;
  the only requirement is running as Administrator.

## Privacy & security — what this app actually does

This app needs Administrator rights and touches the network, so here is the full
picture, plainly:

- **Why Administrator?** Reading inbound packets without a capture driver requires a
  Windows raw socket (`SIO_RCVALL`), which Windows only grants to elevated processes.
  Elevation is used for that single purpose.
- **What is captured?** Inbound UDP traffic of *your own* machine, in memory. Once
  the ACR server is detected, only its packets are decoded; everything else is
  ignored. Nothing is written to disk unless *you* enable pcap recording (and then it
  is a local file you choose). Note that the raw socket sees **all** inbound traffic,
  so a debug `.pcap` contains more than game packets — treat capture files as
  sensitive and think twice before sharing one.
- **What is decoded?** Exactly what the game already shows in the lobby: driver
  pseudonyms, stage times, penalties, stage name, nation, car model. The app does not
  display account identifiers or any real-name field.
- **What leaves your machine?** Nothing, by default. The app has no telemetry and
  uploads nothing. Its only outbound connections are: (1) a one-time download of the
  pinned, checksum-verified `cloudflared` binary from Cloudflare's official GitHub
  releases — fetched automatically in the background at first launch, even if you
  never publish; (2) the Cloudflare tunnel itself, *only if you click Publish* — from that
  moment the leaderboard page is reachable by anyone who has the link, until you
  close the app. If the app dies without closing normally (crash, Task Manager kill),
  the tunnel process can outlive it — check for `cloudflared.exe` in Task Manager.
  (Browsers viewing the page also fetch flag images from flagcdn.com.)
- **Who can see the local page?** The embedded web server listens on all network
  interfaces, so anyone on the same LAN/Wi-Fi can open the page while the server
  runs (that is the point — sharing with the lobby). Stop the server (**Start**
  again) when you are done.
- **What it will never do:** send packets to the game server, modify game files or
  memory during play, or interact with the game process in any way. It is a listener.

### Publishing a public link — other players' data

The leaderboard shows other drivers' pseudonyms and, by default, their nationality.
That is **personal data of third parties**. On your LAN, among the people you are
already playing with, this is fine. But the **Publish** button exposes it on the
public internet to anyone who has the link, which is a different responsibility:

- The public link is never automatic — it is a deliberate click, and the app asks
  you to confirm before opening it.
- The **"Hide nationalities (shared view)"** option (Config panel) removes the nation
  flags from the shared page; names and times still show.
- If you publish, share the link only with people who should see it, and close the
  app (or the tunnel) when you are done. Nothing is stored: shut it down and the
  page is gone.

## How it works

```
UDP traffic ──> raw-socket sniffer ──> server auto-detection
                                              │
                                              ▼
               Unreal Engine replication decoding (cleartext)
   names · splits · penalties · stage · nation · car · race phase/timer
        spline distance · lobby phase · stage start time · weather
                                              │
                                              ▼
               session matrix ──> embedded web server ──> browser
                                         │
                                         └──> Cloudflare quick tunnel (optional)
```

ACR is built on Unreal Engine; its multiplayer traffic is standard UE replication,
currently unencrypted in the server→client direction. The app reads that cleartext
stream to surface exactly the values the game already shows in the lobby. Nothing
about the game binary is read, modified, patched or redistributed.

## Getting started

### Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
  (the SDK if you build from source). If it is missing, Windows shows a dialog
  with a direct download link the first time you launch the exe — install it
  once, then relaunch.
- Administrator rights (raw-socket capture)

### Build

```
dotnet build        # debug build
publish.bat         # single-file Release exe in .\publish\
```

### Use

1. Start `ACRLiveTiming.exe` **as Administrator**.
2. Join an ACR multiplayer lobby — the status dot turns green once the server is
   detected (a few seconds).
3. Click **Start** (Local web server panel) and open the local URL; share it on
   your LAN. (Click **Start** again to stop the server.)
4. Optional: fill **Web page title & description** (top-left) to add a heading to the
   shared page and a rich preview when the link is pasted in Discord, etc.
5. Optional: click **Publish** to get a public `trycloudflare.com` URL for the rest
   of the lobby. Wait for the *"link is now live"* log line before sharing; use the
   copy button to grab the link, and click **Publish** again to take it offline.
6. Optional (streaming): with the web server running, use the *Overlays* panel —
   **Classification** and **Progress** each have their own show/hide, always-on-top,
   click-through lock and background-opacity controls. For OBS, click **Copy OBS
   source URL** and add it as a Browser Source (see [Streaming with OBS](#streaming-with-obs)).
   To sit over the game directly, show the overlay window and lock it click-through.
7. Drive. Results appear as drivers finish; totals and ranks update live. Use
   **Reset session** to clear the board between events (it keeps the decoded nations,
   cars and the current stage name so a same-stage restart is not left unlabelled).

## Streaming with OBS

For OBS, use a **Browser Source**, not a window capture — it renders the transparent
page directly. A window capture of a desktop overlay comes out **black**, because the
widget is GPU-composited (WebView2) and the window itself is transparent.

<img src="docs/overlay-classification.png" alt="The classification overlay widget" width="280">

<img src="docs/overlay-progress.png" alt="The live progression overlay widget" width="640">

<sub>The two widgets — classification and live progression — at 90 % background
opacity, over the game they are fully transparent. Demo data.</sub>

In the *Overlays* panel, click **Copy OBS source URL** for the widget you want, then
in OBS:

1. **Sources → + → Browser**, and paste the URL. Add two separate sources for the two
   widgets: `…/?view=classification` and `…/?view=progress`.
2. Set the source **Width** and **Height** in its properties to change how much shows
   — the page re-renders at that size (this is *not* the same as dragging the box in
   the scene, which only scales the bitmap):
   - **Classification** — raise the **Height** to reveal more rows
   - **Progress** — raise the **Width** to lengthen the bar
3. Then position and zoom it in the scene with the normal OBS handles (Transform).

You do **not** need to click **Show** — the desktop overlay windows are independent
of OBS; the Browser Source loads the page straight from the server. Only the web
server has to be running.

The copied URL carries the widget's background opacity (`?bg=`, from the Overlays
panel — `bg=0` is fully transparent). Nation flags load from the internet.

## Antivirus & SmartScreen

The published `.exe` is not code-signed, so on first launch Windows may show one of
two warnings. Both are expected for an unsigned open-source tool — here is what they
mean and what to do.

- **"Windows protected your PC" (SmartScreen).** This only means the publisher is
  unrecognised, not that a threat was found. Click **More info → Run anyway**.
- **Antivirus / Defender flag.** The app trips heuristic detectors because it does
  things malware also does — it opens a raw socket to read packets, runs elevated, and
  ships as a single self-extracting exe. It is a passive listener (see **Privacy &
  security** above): it sends nothing to the game and modifies nothing. If Defender
  quarantines it, it is a false positive — restore it from quarantine, or build the exe
  yourself from source (`publish.bat`).

**Verify your download.** Each release lists a SHA-256 checksum. Confirm the file
matches before running:

```
certutil -hashfile ACRLiveTiming.exe SHA256
```

If you would rather not trust a prebuilt binary at all, clone the repo and run
`publish.bat` — the exe you get is the exe you ran.

## Status & limitations

| Element | Status |
|---|---|
| Server→client traffic | cleartext (unencrypted), decoded |
| Split + final times per driver | ✅ ms-exact vs in-game screen, penalties included |
| Stage name | ✅ |
| Nation + car per driver | ✅ read at JOIN from the player's participant actor (deterministic, no lap time needed); time-anchored binding kept as fallback — screenshot-validated |
| Complete finisher list | ✅ multi-bit-shift scan |
| Finish detection (hide intermediate splits) | ✅ event-driven via the replicated race phase (*Ended*), exact to the ms, streamed live |
| DNF detection | 🟡 live on the running stage (car's *Retire/Disqualify* phase), inferred on closed stages from posted splits without a finish; an early quit (no splits) shows as absent |
| Live stage progression | ✅ horizontal leader↔tail auto-fitting track above the board; markers named at spawn (PlayerState identity block), first-split fallback — can be partial during the first stage after lock-on |
| Per-viewer web settings | ✅ stage exclusion, penalty %, progression window, hide nations, finish gating — recomputed client-side from raw per-stage data; session-only, one-click reset to host config |
| Lobby phase · stage start time · weather forecast | ✅ decoded and shown in the page header |
| Live positions / gaps | 🟡 per-car live position decoded, not yet surfaced as a ranking |

Built against **Assetto Corsa Rally 0.5.1**. A game update that changes the wire format
— or turns on encryption — can break the decoding at any time. The tool reads
**cleartext only** and will never attempt to decrypt an encrypted stream: if ACR
encrypts its traffic, it says so and live timing simply stops working.

## Project layout

```
src/
  Decode/    protocol decoding: UE replication parser, FStrings, result scanner,
             streaming finish/progress detector, nation & car binding, weather
  Net/       raw-socket sniffer, server auto-detection, pcap record & replay
  Model/     engine (state machine) + thread-safe session matrix
  Web/       embedded HTTP server (/, /state) + single-page UI
  Tunnel/    cloudflared runner (pinned version, checksum-verified)
  UI/        WPF control panel + transparent OBS overlay windows + settings
```

## Support the project

ACR Live Timing is free and open source, built on personal time. If you enjoy it and
want to support my work, you can make a contribution — it genuinely helps and is
much appreciated:

- ☕ [Ko-fi](https://ko-fi.com/florentdchamps)
- 💜 [GitHub Sponsors](https://github.com/sponsors/FlorentDChamps)

A star on the repo, a bug report or simply spreading the word already goes a long
way. 🙂

## License

Copyright (C) 2026 Florent DESCHAMPS.

Licensed under the **GNU General Public License v3.0 or later** (GPL-3.0-or-later) —
see [LICENSE](LICENSE). You may use, study, share and modify this software freely, but
any distributed version — including modified forks — must remain open source under the
same license. Closed-source or proprietary redistribution is not permitted.
