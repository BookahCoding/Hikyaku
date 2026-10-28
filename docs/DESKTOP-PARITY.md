# Desktop + Android parity inventory (research)

**Checkpoint:** UX-RESEARCH (design only).  
**Hikyaku HEAD at research:** `13c9e71` (tests 87/87 claimed in prior handoff; not re-run this pass — no app files touched).  
**This document separates upstream findings from Hikyaku recommendations.** Status marks are evidence-based; names alone do not imply implementation.

## Status legend

| Mark | Meaning |
|---|---|
| **implemented** | Present in Hikyaku source with behaviour |
| **fixture-tested** | Covered by automated tests (not live gateway) |
| **live-tested** | Exercised against a real dashboard on a user machine |
| **missing** | Not present in Hikyaku |
| **feasible** | Can be built with existing gateway/RPC or local Windows APIs |
| **needs-protocol** | Needs gateway/RPC/plugin surface not yet consumed (or unknown) |
| **Windows-only** | Sensible only / differently on Windows (no macOS/Linux claim) |
| **needs-new-boundary** | Requires a new security/product boundary (folder grant, terminal, Git publish, etc.) |

## Research sources (pinned)

### Official Hermes Desktop docs

| Source | Retrieved | Pin / revision |
|---|---|---|
| [Hermes Desktop user guide](https://hermes-agent.nousresearch.com/docs/user-guide/desktop) | 2026-10-25 (this run) | Live docs site; content matches GitHub `website/docs/user-guide/desktop.md` |
| GitHub blob `NousResearch/hermes-agent` `website/docs/user-guide/desktop.md` | 2026-10-25 | Blob content SHA `0e59326e5473e629a5b0799ca74dc9168894d7de`; last commit touching path `1249e90c83fa50f36ac025ecbeda34c0b493dee5` (2026-10-25) |
| [Bot Mode guide](https://hermes-agent.nousresearch.com/docs/user-guide/bot-mode) | 2026-10-25 | Live docs |
| [Git worktrees](https://hermes-agent.nousresearch.com/docs/user-guide/git-worktrees) | 2026-10-25 | Live docs (CLI `-w`, `/worktree`, desktop pane cross-ref) |
| [Voice Mode](https://hermes-agent.nousresearch.com/docs/user-guide/features/voice-mode) | 2026-10-26 (CP14) | Dictation / client-direct vs relay; Desktop mic UX |
| BookahHermes `AudioApi.kt` | `5f00986b2a582e480592fd680131d501d57038ef` | `POST api/audio/transcribe|speak`; `GET api/audio/voice-live/status` |

### Public Desktop source

| Source | Pin | Notes |
|---|---|---|
| `NousResearch/hermes-agent` `apps/desktop/` | Latest commit on path at research: `1d0dbb87faa08490b26c0ec5313a2d5592ebb8bd` (2026-10-25) | Electron + React; README/DESIGN present. **Not cloned into this repo.** Inventory below uses **docs** as primary capability list; source tree confirms product exists as first-party Desktop, not a third-party clone. |

### Android (Herald) baseline

| Source | Pin |
|---|---|
| Sibling `../BookahHermes` (public `BookahCoding/herald`) | `5f00986b2a582e480592fd680131d501d57038ef` |
| Hikyaku matrix | [`docs/ANDROID-PARITY.md`](ANDROID-PARITY.md) |

### Progressive-disclosure / guided Git references

| Source | Role |
|---|---|
| [NN/g Progressive Disclosure](https://www.nngroup.com/articles/progressive-disclosure/) (Nielsen, 2006) | Primary/secondary feature split; Advanced must not hide frequent beginner needs |
| [GitHub Desktop — committing & reviewing changes](https://docs.github.com/en/desktop/making-changes-in-a-branch/committing-and-reviewing-changes-to-your-project-in-github-desktop) | Separate review → commit → push; beginner-visible diffs |

### Coverage gaps

- Hermes Desktop **Electron internals** (exact IPC contracts, every RPC method) were **not** exhaustively reverse-engineered from `apps/desktop/src` in this pass — would require cloning and reading the tree. Capability claims for upstream Desktop come from the **official desktop.md** retrieved above.
- **Live** Hikyaku gateway chat/Inbox was **not** re-tested this checkpoint (design-only; prior handoff: live pending).
- Binary/plugin compatibility with upstream Electron Desktop is **explicitly not promised**.

---

## Part A — Research findings (Hermes Desktop / docs)

Upstream Hermes Desktop is a **native Electron app** sharing the same agent core, config, keys, sessions, skills, and memory as CLI/gateway — not a separate product clone. Surfaces documented include:

**Chat & sessions:** streaming chat, drafts, find-in-page, timeline rail, reading-position memory, model/reasoning/fast picker, YOLO per-session, context-usage meter, task progress, branch/edit patterns via agent tools, attachments/images with restore-draft, markdown/reasoning blocks.

**Tools / approvals:** dangerous-command approvals (bypassable by YOLO), Needs-you style interruptions, failed-turn layer cards with Retry / Switch provider / diagnostics consent.

**Voice:** dictation, read-aloud, wake-word ear, full voice conversation; mic permission (esp. macOS).

**Bots / rooms / routines:** Bot Mode roster (Sessions \| Bots), group rooms, @mentions, Routines (cron), hide/unhide bots.

**Inbox / Needs-you:** cron/async completions as timeline disclosures; Bot Mode “needs you” badge on rooms; approvals elsewhere.

**Layout:** tabs, multi-window, panes, Simple vs Advanced **interface mode** (visibility only — not permission policy), status bar, command palette, HUD, Quick Entry (global hotkey), tray minimize.

**Projects / files / artifacts:** file browser, Artifacts gallery, repo discovery scan, project lanes.

**Terminal:** real PTY in sidebar; persist while hidden; multi-terminal tabs; “Add to chat” from selection.

**Git:** review pane (Uncommitted / Branch / Last turn), stage, commit, Commit & Push, Create PR / Ask Hermes, worktrees (Cmd/Ctrl+Shift+B), chat follows agent into worktree.

**Subagents:** live frame above composer — Steer / Stop; steer = queued guidance ack.

**Settings / ops:** providers/accounts, MCP, skills, messaging, cron, memory graph, plugins (desktop + agent halves), gateways multi-connection registry (local / remote / Cloud / SSH), local runtime install, updater (GitHub API + git apply; separate desktop vs backend clocks), analytics/diagnostics with consent.

**Context / permissions (upstream posture):** Webapp access ≈ host OS user authority; folder/workspace via project cwd; repo scan configurable; remote vs local explicit on Gateways page. Terminal is a full shell on the host running Hermes.

---

## Part B — Inventory matrix

Columns: **Upstream Desktop (docs)** · **Android @ 5f00986** · **Hikyaku now** · **Gap class**

### Chat / session / drafts / search / edit / branch / steer / maths / media

| Capability | Upstream Desktop | Android pin | Hikyaku | Gap |
|---|---|---|---|---|
| Session list + search | yes | implemented | **implemented + fixture-tested** | live-tested pending |
| Create / resume chat | yes | implemented | **implemented + fixture-tested** | live-tested pending |
| Streaming prompt | yes | implemented | **implemented + fixture-tested** | live-tested pending |
| Drafts / offline cache | yes (local) | encrypted cache | **implemented + fixture-tested** (DPAPI drafts) | — |
| Cancel / interrupt | yes | implemented | **implemented + fixture-tested** | — |
| Edit / regenerate UI | yes (patterns) | implemented | **implemented + fixture-tested** (16a: Edit/Regenerate on user prompts with a stored row, edit banner + Cancel, explicit destructive-cut confirmation, between turns only, exact restore on gateway refusal, never auto-resent) | live gateway acceptance pending; attachments/terminal context are not re-sent on a rewind |
| Branch (copy chat / branch from here) | yes | implemented | **implemented + fixture-tested** (16b: per-message Branch + Branch chat; row-counted over all stored rows; opens the copy without stealing focus; busy-gated) | live gateway acceptance pending; chats over 25,000 stored rows must be branched whole |
| Steer / btw / bg / stop-and-send | yes (subagent + chat) | implemented | **implemented + fixture-tested** (17a: Send steers while a reply runs; Stop & send; `/btw` and `/bg` cards; subagent Steer/Stop from CP8 unchanged) | live gateway acceptance pending; steer is text-only (no attachments) |
| Queue while a reply runs | yes (gateway) | gateway-side | **partial: app-side queue** (17a) | queued text is kept in this app only; gateway-side queue deferred (needs a per-session event pump) |
| Markdown / code / tables | yes | implemented | **implemented** (lite) | — |
| Maths typesetting | yes | implemented (JLaTeXMath) | **implemented + fixture-tested** (WPF-Math 2.1.0 local; CP13b bounded measure-before-alloc, generation abandon, async MarkdownBlock; currency/code/streaming heuristics; unsupported → source fallback) | live chat maths pending live gateway; not all LaTeX macros; no cooperative cancel in library |
| Media upload/download/previews | yes | implemented | **implemented + fixture-tested** | — |
| Find in transcript | yes | ? | **implemented + fixture-tested** (16c: Ctrl+F, literal case-insensitive search over the **loaded** messages, Next/Previous with wrap, message-level mark, Enter / Shift+Enter / Esc) | no per-word highlight; messages not yet loaded (history loads the newest page only) are not searched; no whole-chat server search |
| Long history: load earlier + virtualized list | yes | implemented | **implemented + fixture-tested** (18a: newest page first, explicit **Load earlier messages** for the chat's own profile, de-duplicated by row id, bounded at 5000 rows, only between turns; list virtualized: 1000 messages lay out in ~55 ms instead of ~4.9 s) | live gateway test pending |
| Timeline rail / reading position | yes | — | **missing** (the reader keeps their place when earlier messages load; no saved position, no rail) | feasible |
| Model / reasoning / fast | yes | implemented | **implemented + fixture-tested** | live-tested pending |

### Tools / requests / Needs-you

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Tool activity UI | rich | richer | **minimal** one-line | feasible |
| Approvals / clarify / sudo / secret | yes | implemented | **Needs-you UI + respond; fixture-tested** | live-tested pending |
| YOLO bypass | yes | ? | **implemented + fixture-tested** (per-chat `config.set yolo`; confirm on enable; not three session modes) | live-tested pending; profile Smart/Manual/Off separate |
| Failed-turn layer cards | yes | — | **missing** (generic banners) | feasible |

### Voice

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Dictation (explicit mic → preview → transcribe → composer) | yes | implemented | **implemented + fixture-tested** (CP14/14b lifecycle) | live mic + live `/api/audio` pending; status-route failures never gate STT (15a), transcribe-route 404 gates; draft cross-scope + WaveIn seam fixed in 14b |
| TTS / speak playback | yes | implemented | **partial + fixture-tested** (CP14b cancel/session/bounds; Stage 30 per-reply Read aloud / Stop reading) | live speak pending; MPEG ephemeral temp only; no auto-read, voice or speed choice |
| Voice-live / wake word | yes | live / not started | **pending / not targeted** | WebRTC live + wake not this slice |
| Phone call tools | — | implemented | **companion-phone-only** | — |

### Bots / rooms / routines

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Bot roster / rooms / @mention | Bot Mode | implemented | **implemented + fixture-tested** (roster/canonical Bot Chat; Desktop rooms read-only mirror; Stage 32: gateway-hosted rooms page with @mention picker, writes) | live-tested pending; UI filter ≠ server auth; Desktop-run rooms stay read-only |
| Routines / cron CRUD UI | yes | implemented | **implemented + fixture-tested** (22: Scheduled page, All profiles, owner-profile actions, picker + Advanced; routines are listed with a Routine tag, no docked Routines pane) | live-tested pending |
| Hide bot / group rooms | yes | yes | **partial** (hidden bots omitted from roster; rooms restricted when member deselected) | hide/unhide write + live pending |

### Inbox / Needs-you (product split)

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Cron followed Inbox | disclosures / cron pane | implemented | **implemented + fixture-tested** | live-tested pending; herald-push plugin |
| Needs-you badge (approvals) | yes | implemented | **implemented + fixture-tested** | Prefer **contextual badge** not primary nav forever — see UX-PLAN |
| Phone push / ntfy | — | implemented | SetupRequired / pending | companion |

### Tabs / windows / panes / HUD / Quick Entry

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Multi-tab sessions | yes | limited | **missing** (single main chrome) | feasible |
| Multi-window / pop-out | yes | — | **missing** | Windows-only feasible |
| Simple / Advanced layout mode | yes (visibility) | — | **implemented + tested** (20b: visibility only; Simple hides Reasoning / Fast / YOLO chips and raw tool-activity lines; a chat with YOLO on keeps its indicator; new installs start Simple, existing installs keep the full chrome; Settings → Experience and the palette toggle it) | no per-mode saved layout yet (there is little layout to remember); MCP / skills / memory-graph / plugin admin surfaces do not exist yet, so there is nothing more to hide |
| HUD / Quick Entry / global hotkeys | yes | — | **missing** | Windows-only; needs-new-boundary (OS hooks) |
| Command palette | yes | — | **implemented + tested** (20a: Ctrl+K; literal ranked matching; chats and gateway sessions by name; navigation and reversible view actions only, never anything that sends, deletes, grants, publishes or changes a permission; unavailable commands are not listed) | no recent-items memory, no theme switching from the palette, no user-defined commands |
| Status bar meters | yes | — | banner only | feasible |

> **Deliberate divergence (the maintainer, 2026-10-27):** Hermes Desktop keeps Capabilities and Insights visible in Simple mode. Hikyaku hides them (and Bots, Rooms, Journey, Projects, Scheduled, Artifacts, the terminal and the technical chips) in Simple, because Simple is for a non-technical user and Advanced is for managing Hermes. This visibility instruction overrides "copy Desktop" (see Stage 36 in `HANDOFF.md`).

### Artifacts / file browser / projects

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| File browser / preview | yes | limited | **implemented** (grant-scoped; text preview) | write needs confirm; no auto backups |
| Artifacts gallery | yes | — | **partial + fixture-tested** (Stage 33: built from stored chats on request; Preview / Save / Copy link / Open chat; hidden in Simple) | no documented endpoint, so no cross-chat index beyond the 15 most recent; no tool-completion suggestions; links not opened |
| Projects sidebar / repo scan | yes | — | **implemented** (explicit grant; no silent scan) | revoke per profile |
| Attachment picker only | — | yes | **implemented** (+ project share review) | folder select ≠ upload |

### Terminal

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Embedded real terminal | yes (PTY) | no desktop PTY | **implemented + fixture-tested** (CP11/11b/12c ConPTY; local only; default off; resize wired; interactive + one-shot drain-after-exit) | **not** folder-gated; remote unavailable (no verified protocol) |
| Persist while hidden | yes | — | **implemented** | explicit Close kills PTY |
| Add selection to chat | yes | — | **implemented + fixture-tested** (CP11b: stage→next Send; not History-only bubble) | redacted preview + destination revalidation; keys hard-blocked; pending TTL/discard |

### Git review / commit / push / worktree

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Diff review pane | yes | — | **implemented** (Changes) | ArgumentList git; no shell |
| Commit (checkpoint) | yes | — | **implemented + fixture-tested** (CP10) | local Save checkpoint; **separate from push** |
| Push / Create PR | yes | — | **implemented + fixture-tested** (CP12/12b: network grant; effective PUSH URL pin across preview/push/ls-remote/reconcile/PR; untrusted config fail-closed; owned empty hooks; strict URLs; durable uncertain gate; bare-remote + fixture/loopback; live GitHub auth setup-required) | live PAT/PKCE not wired; no publish of this checkout; OS helper auth not implicit on guided push |
| Worktrees / parallel lanes | yes | — | **implemented + fixture-tested** (CP10) | destination consent; no force-remove default |
| Destructive reset / silent push | present as power actions upstream | — | **must not auto** | policy in PERMISSIONS-DESIGN |

### Subagent monitoring

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Live roster / Steer / Stop | yes | partial | **implemented + fixture-tested** (frame in chat; `session.steer` / `subagent.interrupt`; queued ≠ child-read) | live-tested pending |

### Context permissions / local folder / remote identity

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Explicit folder grant | project cwd / picker | phone scoped | **implemented + tested** | no whole-drive/home; UNC/ADS/junction deny |
| Profile gate / selected-profile hide | profiles + Applies to | NotificationGate | **implemented + fixture-tested** | — |
| Local PC vs remote gateway identity | Gateways registry | URL+profile | **identity strip** includes local path/branch when granted | remote never uses local Explorer path |
| Terminal ≠ folder grant | documented host shell | — | **implemented** (CP11) | folder grant / YOLO / Advanced do not authorise shell |

### Settings / providers / config / skills / MCP / messaging / cron / analytics / memory

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Gateway URL + password auth | yes | yes | **implemented + fixture-tested** | — |
| PKCE / OAuth browser | yes | implemented | **implemented + fixture-tested** (15b: explicit button, default browser at the gateway's authorize URL only, `127.0.0.1` callback, `state` check, 10 min timeout, DPAPI per-gateway tokens, `/api/` bearer only) | live sign-in against a real gateway pending; Cloudflare Access / non-`native_pkce` gateways are shown as unsupported and fall back to password; no server revoke exists |
| Providers / keys UI | rich | yes | thin Settings | needs-protocol |
| Skills / MCP / config.yaml UI | yes | yes | **skills / toolsets / MCP switches + MCP test implemented, fixture-tested** (24) plus approvals.mode (Smart/Manual/Off); no hub, no MCP add/edit, no config.yaml editor | needs-protocol for the rest; live-tested pending |
| Messaging setup | yes | yes | **missing** | needs-protocol |
| Cron job manager | yes | yes | **implemented + fixture-tested** (22; follows Desktop's Cron page; hidden in Simple like Desktop) | live-tested pending |
| Keyboard shortcuts | yes (rebindable) | — | **partly implemented** (23: Ctrl+N/K/P/,/Shift+F/B/F/`//; no rebinding, no tab/window/HUD keys) | fixture-tested table |
| Follow newest message while streaming / reading position | yes | yes | **implemented + tested in the real window** (28: follow at the bottom, no yanking, Jump to latest); no saved per-chat reading position, no timeline | live-tested pending |
| Failed-turn recovery | yes | yes | **implemented + tested** (29: plain-words card, delivery honesty, put-back-in-box only when safe; no automatic resend) | live-tested pending |
| Chat context menu (rename / pin / archive / read state / delete) | yes | yes | **implemented + fixture-tested** (27: gateway rows only; Archived view; delete confirmed) | live-tested pending |
| Memory graph | yes | read-only (`JourneyApi`) | **partial + fixture-tested** (Stage 34 "Journey": month timeline, All / Used / Learned, node text; read-only) | no zoomable graph, playback or layout share; no edit / delete (no wire call documented); live payload unproven |
| Insights / analytics | yes | insights | **implemented + fixture-tested** (25: 7/30/90 days, totals, daily chart, top models / tools / skills; billed vs estimated cost labelled) | live-tested pending |
| Toast / notifications | yes | push | **implemented + fixture-tested; real OS path observed on the dev PC** (19a-c: per-user notification identity registered only when you turn notifications on; toast confirmed by Action Center history; toasts for Needs-you requests and for Inbox Notify via a bounded poll; kind/job + profile only, never message, command or output text; silent while the window is in front; throttled) | not verified: banner visibility to a person (Focus assist), **click-through / deep link / cold start** (needs a COM activator for an unpackaged app), a live gateway; Inbox toasts need herald-push and the Notify follow, and poll every 2 min only while notifying is wanted |
| Cloudflare Access token | — | implemented | **implemented + fixture-tested** (21: service Client ID/Secret saved per host in DPAPI; sent as `CF-Access-Client-Id/-Secret` on every request to that host over https only, never plain http/ws, never another host, never via a redirect; Access blocks detected and explained; Settings Save / Remove / Test) | not verified against a real Cloudflare Access application; the wss upgrade header is covered at the provider level (TLS needed for an end-to-end check); an Access *login* (browser/SSO) flow is not implemented, only service tokens |

### Local runtime / updater / plugins

| Capability | Upstream | Android | Hikyaku | Gap |
|---|---|---|---|---|
| Bundle/manage local Hermes runtime | yes | N/A | **not targeted** as full desktop-core; companion client | product choice |
| GitHub Releases updater | yes (Hermes) | Play | **implemented + fixture-tested** [`UPDATES.md`](UPDATES.md): manual check, signed (project key) download, two confirmations, transactional swap; **install disabled until a publisher key is embedded** | no real release installed; private-repo token path and installer/MSIX not done |
| Desktop plugins SDK | yes | phone plugins | **not targeted** (no sandbox claim) | avoid false parity |
| Phone plugins | — | many | **companion-phone-only** | — |

---

## Part C — Honest summary

| Area | Verdict |
|---|---|
| Daily chat companion (sessions, send, markdown, attachments, Inbox, Needs-you, models, reconnect, toasts, profile gate) | **Strong Hikyaku baseline** — fixture-tested; **live** still pending |
| Full Hermes Desktop (terminal, Git, worktrees, bots, HUD, plugins, local runtime, memory graph) | **Broad research target**, not a binary/plugin compatibility promise |
| Android matrix | Still the phone companion gate; Windows must not enable phone-only plugins |
| Next design docs | Placement → [`UX-PLAN.md`](UX-PLAN.md); security → [`PERMISSIONS-DESIGN.md`](PERMISSIONS-DESIGN.md); sequence → [`IMPLEMENTATION-STAGES.md`](IMPLEMENTATION-STAGES.md); mock → `design/` |

**Attribution:** Hermes Agent / Hermes Desktop docs and public source © Nous Research (see their LICENSE when shipping notices). Herald / BookahHermes @ pin above. Hikyaku recommendations in sibling docs are **not** upstream claims.
