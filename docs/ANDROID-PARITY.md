# Android (Herald / BookahHermes) → Windows (Hikyaku) parity matrix

**Android reference revision:** `5f00986b2a582e480592fd680131d501d57038ef` (sibling `../BookahHermes`, public source only).  
**Evidence:** `docs/ROADMAP.md` (Android), BookahHermes `shared/core` (auth, connection, rpc, sessions, chat, inbox, profiles), Hikyaku fixtures.  
**Rule:** unchecked Android roadmap items are **not** implemented parity targets. Phone-only plugins/permissions stay off on Windows.

Status legend:
| Status | Meaning |
|---|---|
| **implemented** | Present in Hikyaku with source-backed behaviour |
| **tested** | Covered by automated tests in this repo (not live gateway) |
| **pending** | Not built yet |
| **Windows-equivalent** | Desktop analogue planned/possible without phone APIs |
| **companion-phone-only** | Requires phone hardware, OS permissions, or Herald phone plugins — not for this client |

**Identity gate (all surfaces):** every data/search/cache/notification/deep-link path must use explicitly selected `(gateway, profile)` keys via `NotificationGate` / `ProfileSelectionService`. Fail closed before load, empty selection → onboarding, unknown/new profiles denied until opted in.

---

## Connection & auth

| Feature | Android @ pin | Hikyaku | Notes |
|---|---|---|---|
| Dashboard URL normalize | implemented | implemented (settings URL) | http/https only |
| `GET /api/status` probe | implemented | pending | |
| Password login + cookie jar | implemented | implemented + tested (handler + loopback) | DPAPI password; cookies in-process |
| Native PKCE browser sign-in | implemented | **implemented + fixture-tested** (15b) | RFC 8252 loopback + S256, bearer + single-flight refresh, DPAPI token store; **live gateway sign-in not yet accepted**. Android's own roadmap lists nothing further here |
| WS ticket `POST /api/auth/ws-ticket` | implemented | implemented + tested | Ticket never in URL |
| `/api/ws` + subprotocols | implemented | implemented (ClientWebSocket) + fixture-tested | Live WS pending user test |
| `gateway.ready` + `client.capabilities` | implemented | implemented + tested | |
| Reconnect backoff + fresh ticket | implemented | **implemented + tested** (`GatewayConnection` loop; fresh ticket; no auto-resend) | Live pending |
| Cloudflare Access service token | implemented | **implemented + fixture-tested** (21) | Same header contract and detection rules as `AccessTokens.kt`; live Access application not tested |
| Legacy static session token | not started on Android | pending / not targeted | |

## Sessions & chat

| Feature | Android @ pin | Hikyaku | Notes |
|---|---|---|---|
| Session list REST | implemented | **implemented + tested** (paged Recent + profile) | Live pending |
| `session.create` / resume | implemented | **create + resume implemented + tested** | History via REST `messages` |
| `prompt.submit` streaming | implemented | implemented + tested (fixtures) | Live pending |
| Cancel / `session.interrupt` | implemented | **implemented + tested** (incl. cancel-while-waiting) | |
| Cross-profile session identity | implemented | **implemented + tested** (stream + resume + attach queue) | |
| Steer / btw / bg / stop-and-send | implemented | **implemented + fixture-tested** (17a) | `session.steer`, `prompt.btw`, `prompt.background`; typed `/btw`, `/bg`; answers by event; live gateway test pending |
| Queue (send while a reply runs) | gateway-side (`queued:true`) | **partial: app-side queue** (17a) | Held in this app and sent after a clean finish; lost if the app closes; a stop/error/chat switch hands the text back, never sends. Gateway-side queue needs a long-lived per-session event pump (deferred) |
| Edit / regenerate (rewind via `prompt.submit truncate_before_row_id`) | implemented | **implemented + fixture-tested** (16a) | Confirmed before any cut; between turns only; refusal restores the transcript exactly; no live gateway test yet |
| Branch (`session.branch_whole` / `session.branch`, branch-from-here) | implemented | **implemented + fixture-tested** (16b) | Counts every stored row as the gateway does; incomplete read refuses; non-destructive (no confirmation); live gateway test pending |
| Sidebar search | implemented | **implemented + tested** (profile-gated `SessionCatalog`) | |
| Running filter | implemented | **implemented + fixture-tested** (16d Running; 26 Needs you = waiting or unread) | `session.active_list` polled only while a filter is on; one poll serves both chips; opening a chat clears the unread flag (27) |
| **Needs-you** (approvals/questions across bots) | implemented | **implemented + tested** (fixtures; live pending) | Separate from cron Inbox |
| Markdown / code / maths | implemented | **markdown/code/tables + local WPF-Math typesetting** (MathMarkup parity; CP13b bounded renderer + async UI fallback) | No unsafe HTML/fetches; no remote TeX |
| Tool activity UI | implemented | minimal one-line ToolActivity | |
| Approvals / clarify / sudo / secret / vault | implemented | **Needs-you UI + respond**; live pending | Secrets never logged/cached |
| Attachments / media / previews | implemented | **upload/download + cards + tested** | Explicit picker; no folder bridge |
| Models / thinking / fast | implemented | **implemented + tested** (catalog usable≠entitlement; pending new-chat; config.set; confirm expensive) | Live pending; bots/rooms still pending |

## Inbox (cron followed outputs) — separate from Needs-you

| Feature | Android @ pin | Hikyaku | Notes |
|---|---|---|---|
| Inbox of followed cron runs | implemented | **implemented + fixture-tested** | Via `command.dispatch` / herald-push; SetupRequired if plugin absent |
| Per-job Off / quiet Inbox / Notify | implemented | **implemented** (Following API + UI refresh) | Profile-gated; needs plugin ≥ 0.4.1 for All/Off |
| Failures + unread badge | implemented | **implemented + tested** (filter + unread in store) | Gate via NotificationGate |
| Job chips + My groups filters | implemented | **job chips + local groups filter + follow settings UI** | Groups remain local (not herald-push) |
| Offline retained runs | implemented | **implemented + tested** | Keyed by gateway URL |
| Preserve existing job delivery (Telegram etc.) | implemented | N/A / do not break | Windows must not claim to replace gateway delivery |
| herald-push plugin dependency | implemented | companion consideration | **No server install/restart from this client** |
| Phone push `cron_run` / ntfy registration | implemented | pending / SetupRequired without plugin | Desktop device_id for WS cron only |

## Bots, rooms, automation, config

| Feature | Android @ pin | Hikyaku | Notes |
|---|---|---|---|
| Bots roster / rooms / @mention | implemented | **implemented + fixture-tested** (CP8 Desktop rooms read-only; Stage 32 hosted rooms `groups.*` writable with @mention picker, stop / rename / delete, allow-once / deny; selected-profile gate) | Live pending; room create + files + unread / toasts not done |
| Scheduled jobs CRUD | implemented | **implemented + fixture-tested** (22) | Wire contract from `CronApi.kt`; UX follows Hermes Desktop's Cron page instead of Android's, where they differ |
| Capabilities / skills / MCP / config.yaml | implemented | **skills, toolsets, MCP switches + test implemented + fixture-tested** (24) | Contract from `CapabilitiesApi.kt`; config.yaml editor, skills hub and MCP add/edit pending |
| Insights / usage analytics | implemented | **implemented + fixture-tested** (25) | Same route; Desktop's layout |

## Voice

| Feature | Android @ pin | Hikyaku | Notes |
|---|---|---|---|
| Dictation (mic → preview → `/api/audio/transcribe` → composer text) | implemented | **implemented + fixture-tested** (CP14; explicit opt-in; no autosend) | Live mic + live gateway pending; AudioApi wire = BookahHermes `AudioApi.kt` |
| Speak / TTS playback | implemented | **partial + fixture-tested** (local WAV/MPEG player; speak client; Stage 30 per-reply Read aloud) | Live speak pending |
| Voice-live (GPT-Live / WebRTC) | implemented | pending / capability-gated | Status probe only (separate from dictation since 15a); session not implemented |
| Wake word | not started | not targeted | |
| Phone call tools | implemented | companion-phone-only | No phone permissions on Windows |

## Offline cache & notifications

| Feature | Android @ pin | Hikyaku | Notes |
|---|---|---|---|
| Encrypted offline chat cache | implemented | **implemented + tested** (DPAPI/AES-GCM; drafts) | Keyed gateway+profile; stale banner |
| Local notification sink boundary | implemented (push) | **implemented + tested** (`NotificationSink` + gate) | Gate ≠ OS toast |
| Windows toast delivery | phone push | **implemented + tested; observed in Action Center history on the dev PC** (19a: native WinRT API, consented per-user registration, history-verified; Needs-you toasts 19b; Inbox Notify by polling 19c — Windows has no push channel) | Synthetic runs label **Observed/NotObserved/Unavailable**; not personal chats |
| Cold-start / deselect / unknown deny | implemented | tested | |
| E2E push outside network | implemented | pending / optional | |
| Deep links | implemented | **fail-closed tested** (toast/deep-link without profile denied) | Full URI handler UI still thin |

## Theme / identity

| Feature | Android @ pin | Hikyaku | Notes |
|---|---|---|---|
| Exact Herald/Ukiyo-e tokens | implemented | **applied** to WPF Tokens + light/dark toggle | MIT fonts local; see THEME-REFERENCE |
| Familiar ChatGPT-style chrome | product requirement | **sidebar / conversation / composer / settings** | Not an admin dashboard |

## Local PC folder workspace (Checkpoint 9)

| Feature | Status | Notes |
|---|---|---|
| Explicit folder grant | **complete** | Per installation/gateway/profile; picker; no whole-drive/home; revoke |
| Confirm overwrite/delete | **complete** | Every overwrite/delete; **no** automatic backups |
| Automatic backups | **not targeted** | No auto-backup subsystem |
| Cloud share of selected content | **complete** (review gate) | Deliberate attach/share; suspected keys hard-blocked (ack ≠ share); folder select ≠ upload |
| Read-only Git Changes | **complete** | ArgumentList git status/diff; `--no-ext-diff` |
| Guided Git commit / worktrees | **complete** (CP10) | Exact-file stage; Save checkpoint local only; worktree create/list/remove |
| Guided Git push / PR | **complete transport** (CP12) | Network grant; exact remote confirm; bare-remote + fixture/loopback PR; live GitHub auth setup-required |
| Terminal opt-in | **complete** (CP11/11b/12c corrections) | Separate boundary — ConPTY local; default off; not implied by folder grant; remote unavailable; stage-to-Send share |
| Cross-profile / keys-secrets | denied | Must not leak; ack does not share keys |
| Gateway FS tool/plugin | **NOT deployed** | See `docs/GATEWAY-FS-BRIDGE-PROPOSAL.md` |

## Phone plugins & permissions

| Feature | Android @ pin | Hikyaku |
|---|---|---|
| All phone tools (SMS, contacts, location, …) | implemented, default off | **companion-phone-only** — not enabled |
| Wear OS | implemented | companion-phone-only |

---

## Updater

| Feature | Status | Notes |
|---|---|---|
| GitHub Releases check/install | **implemented + fixture-tested; install disabled until the owner embeds a publisher key** (15c) | See `docs/UPDATES.md`. Android has no in-app updater of its own; no real release installed yet |

## Honest summary for this checkpoint

- **Checkpoint 5:** reconnect loop + fresh ticket, delivery uncertainty (never auto-resend), model/reasoning/fast controls, Windows toast path with profile gate + disable, synthetic UI acceptance screenshots, `docs/USER-ACCEPTANCE.md`.
- **Checkpoint 8:** Bot roster / canonical Bot Chat / Desktop rooms (gated) / subagent Steer·Stop honesty; sidebar visual cleanup; synthetic UI captures.
- **Checkpoint 9:** Local Projects grant/revoke, path boundary, Files/Preview/Changes (read-only Git), secret share review; no shell/gateway plugin.
- **Checkpoint 10:** Exact-file stage/unstage, Save checkpoint (local commit ≠ push), separate working copies (worktrees) with destination consent + dirty refusal; hooks overrides documented; keys exclusion (ack ≠ share).
- **Checkpoint 11:** Explicit local ConPTY terminal opt-in (default off; not folder-gated; not a gateway shell bridge); remote unavailable with truthful copy.
- **Checkpoint 12/12b:** Guided Push (network grant; effective PUSH URL pin; untrusted config fail-closed; owned hooks; strict URLs; durable uncertain gate; disposable bare-remote + ls-remote) and PR fixture/loopback transport; live GitHub auth setup-required.
- **Checkpoint 14:** Explicit mic opt-in + preview/cancel/transcribe/insert; source-backed `AudioApi` (`/api/audio/transcribe|speak`, voice-live status probe); fixture-tested; live mic/gateway pending; wake/voice-live session not this slice.
- **Stage 29:** Failed-reply recovery cards (delivery in words; put the message back in the box only when it is known not to have arrived or the gateway reported the failure); never an automatic resend.
- **Stage 28:** Reading position: chats open at the newest message, a streaming reply stays in view while the reader is at the bottom, scrolling up is respected, Jump to latest; real-window acceptance.
- **Stage 27:** Chat actions (rename, pin, archive, mark read / unread, delete) with the mutation-snapshot handshake, an Archived view, mark-read on open; fixture-tested, no live gateway.
- **Stage 26:** "Needs you" session-list filter (waiting on you, or an unread reply), sharing the Running filter's single poll; fixture-tested.
- **Stage 25:** Insights (usage analytics) from `InsightsApi.kt`: totals, daily chart, top models / tools / skills; fixture-tested, no live gateway.
- **Stage 24:** Capabilities page (skills, toolsets, MCP servers: switches, MCP test). Contract from `CapabilitiesApi.kt`; confirmations for widening and for tests; fixture-tested, no live gateway.
- **Stage 22:** Scheduled jobs (list with All profiles, pause / resume, run now, edit, delete, new with a structured schedule picker + Advanced). Contract from `CronApi.kt`, behaviour from Hermes Desktop; fixture-tested, no live gateway.
- **Stage 21:** Cloudflare Access service token (per-host DPAPI store, https-only headers, no cross-host or redirect leakage, Access-block detection, Settings Save/Remove/Test). Fixture + real-loopback tested; no live Access application.
- **Stage 19a–c:** real Windows toast path (native API, consented per-user registration, history-verified), toasts for Needs-you requests, and Inbox Notify via a bounded poll. Fixture-tested; observed on the dev PC; click-through/deep links not done.
- **Stage 18a:** History paging (explicit *Load earlier messages*, row-id de-duplication, 5000-row bound) and a virtualized chat list; tool/blank step rows no longer render as empty bubbles; the chat's own profile is used to read its history. Fixture-tested.
- **Stage 17a:** Mid-turn sends — Steer, app-side Queue, Stop & send, `/btw` and `/bg` side tasks. Fixture-tested; the gateway-side queue is deferred.
- **Stage 16d:** Running filter for the gateway session list (`session.active_list` poll + event nudges, METHOD_NOT_FOUND stops asking, honest notes, never hides chats on unknown/unsupported/error). Fixture-tested.
- **Stage 16c:** Find in chat (Ctrl+F) over loaded messages. Fixture-tested.
- **Stage 16b:** Branch (whole chat / from a message) and a REST-shape fix: session list/history now accept the real gateway's numeric row ids, float-second timestamps, lenient booleans and parts-array content (previous fixtures would have thrown against a live gateway).
- **Stage 16a:** Chat edit/regenerate (rewind): row ids from history and `user_row_id`, `prompt.submit` truncation with both confirmations, busy 4009 / stale 4018·4030·4004 handling, confirmation dialog, exact restore on refusal. Fixture-tested; live pending.
- **Stage 15c:** GitHub Releases updater — check, allow-listed bounded download, ECDSA P-256 project-key signature binding version+platform+hash, safe extraction, transactional apply; install fail-closed until a public key is embedded. Fixture-tested.
- **Stage 15b:** Native browser PKCE sign-in (`/auth/native/authorize|token|refresh`, `auth_flows: native_pkce`) — fixture-tested incl. real loopback listener; live sign-in pending.
- **Stage 15a:** Voice capability split — voice-live status no longer gates dictation/TTS (`AudioProbe`); transcribe-route 404 is the authoritative unsupported signal. Fixture-tested; live pending.
- **Checkpoint 14b:** Voice lifecycle correction — cross-scope draft ownership, non-blocking dispatcher paints, WaveIn session/wall-clock/partial-cleanup seam tests, AudioApi capability honesty + size bounds + playback session/cancel. Live mic/gateway still pending.
- **Still pending:** live user send/Inbox/bots; live voice hardware + live `/api/audio`; live PKCE sign-in acceptance; owner key + real signed-release install test; composer @mention polish; live GitHub PR auth; gateway-local bridge deployment. Maths typesetting is **implemented** locally (fixture/WPF visual + synthetic UI); live authenticated chat still pending.
- **Android feature parity:** matrix is the gate, not a completeness claim.
