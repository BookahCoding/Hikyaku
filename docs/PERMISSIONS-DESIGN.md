# Permissions design — local folders, terminal, Git publish (design)

**Status:** design settled; local folder grants (CP9), guided Git (CP10), local ConPTY terminal opt-in (CP11), and guided Push/PR boundary (CP12) are implemented against this document. Live GitHub auth for PR create remains setup-required.  
**Rule:** UI visibility is **not** authorisation. Hiding Advanced does not reduce grants; showing a path does not grant it.

## Boundaries (distinct)

| Boundary | What it authorises | What it does **not** |
|---|---|---|
| **Profile / session selection** | Which gateway+profile data may load, notify, cache | OS filesystem or shell |
| **Local folder grant** | Read/list (and optionally write) under an explicitly chosen root | Whole drive, sibling escapes, automatic shell |
| **Destructive file confirmations** | User-approved overwrite/delete of specific paths | Silent agent overwrite |
| **Cloud share consent** | Intentional outbound share of selected content | Silent upload of keys/secrets |
| **Direct terminal opt-in** | Interactive shell (and agent shell tools if tied) on a declared host | Obeying folder-only gate |
| **Git checkpoint (commit)** | Local history snapshot | Remote publication |
| **Git push / PR / release** | Public or remote publication | Silent or auto push |
| **Remote gateway session** | Agent/tools on **that** host’s OS account | Local PC folder unless separately granted |

## Browser sign-in (auth boundary, Stage 15b)
Separate from folder, terminal, network and YOLO grants; never implied by them. Starts only from the Settings button, and only if `/api/status` advertises `native_pkce`. The system browser is opened only at the configured gateway's `/auth/native/authorize` URL (`BrowserLaunchPolicy`). The callback listener binds `127.0.0.1` on a free port, accepts only `GET /callback` with a loopback `Host` and a bounded query, ignores any request whose `state` is not this attempt's, and times out after 10 minutes; verifier and state live only for that attempt. Bearer tokens are stored per gateway with DPAPI (current user), attached to that gateway's `/api/` calls only (never `/auth/*`, other origins or the WebSocket upgrade, which uses a ticket), never logged or put in URLs. Sign-out forgets them locally; the gateway has no revoke. Password sign-in and browser sign-in replace each other.

## Updates (execution boundary, Stage 15c)
Separate from every other grant. Checks run only when the user clicks. A package is downloaded only if a publisher public key is embedded, and is run only after its ECDSA P-256 signature (bound to version, platform and zip hash) verifies; the second confirmation is a separate dialog. The only code ever executed is the verified new build, as a file-swap helper, from the app's own update folder. Never: silent install, downgrade, unsigned run, embedded token, or touching `%APPDATA%\Hikyaku`.

## Cloudflare Access service token (credential boundary, Stage 21)
Optional and explicit: a Client ID and Secret you paste in Settings, stored encrypted (DPAPI) per gateway host. The pair is sent only as `CF-Access-Client-Id` / `CF-Access-Client-Secret`, only over https (never plain http or ws), only to the host it was saved for, and never to another host: redirects are followed by the app itself, same-site GET only, so a redirect cannot carry the secret elsewhere, and a redirect to the Access login page is reported instead of followed. Values are validated (visible ASCII, no spaces or line breaks, length-bounded, so they cannot inject headers), a token is refused for a non-https address, messages and exceptions never contain the ID or secret, and the secret is cleared from the window once saved. It is separate from the dashboard password, a browser sign-in and the Needs-you secrets.

## Notifications (OS boundary, Stage 19)
Off until the user turns them on in Settings. Turning them on writes exactly one per-user registry entry (`HKCU\Software\Classes\AppUserModelId\Hikyaku.Desktop`, a display name, validated id) so Windows will show this app's toasts; turning them off deletes it. No machine-wide change, no shortcut, no administrator rights. Toasts are gated per profile like every other surface, built from cleaned and bounded text (kind or job name, outcome, profile; never a command, question, secret prompt or run output), throttled, and silent while the window is in front. Scheduled-job toasts mean polling the Inbox every two minutes, only while a Notify job or the failures flag asks for it.

## Local folder grants

1. User picks a folder via OS picker (or typed path with confirm). No silent home-directory scan as a grant (optional **discovery** may list candidates; opening still requires grant).
2. Store grant as `(profileKey, rootPath, mode)` where mode ∈ `{read, readwrite}`.
3. Default mode: **read** until user enables write for that root.
4. **Overwrite / delete:** always confirm with path + action; no batch “allow all deletes for session” without an explicit Advanced session toggle that itself confirms once with countdown.
5. **NO automatic backups** of granted trees (unchanged product rule).
6. **Revocation:** Settings → Projects → Revoke. Immediate: stop file tools, close previews, clear in-memory listings; do not delete user files.
7. **Path / junction escapes:** resolve to canonical path; refuse if result leaves the grant root (junctions, `..`, symlinks). On failure: deny + explain, do not follow.
8. **Keys / secrets exclusions:** never offer `.env`, `*.pem`, `*credentials*`, browser cookie DBs, SSH private keys as “share to cloud” defaults. Detection is **heuristic and incomplete** — UI must say: “We try to spot secrets; we can miss some. You are responsible for what you send.” **Acknowledging a warning does not share** suspected keys or credential leaves — acknowledgement only records that the user saw the block (timestamp locally; secret not logged). Staging refused for the same classes.

## Profile / session grants

- Existing `NotificationGate` / `ProfileSelectionService` remain fail-closed: empty selection → onboarding; unknown profiles denied until opted in.
- Folder grants and terminal opt-ins are **keyed by selected profile** (and gateway identity). Switching profile hides other profiles’ grants from UI and runtime.
- Cross-profile leakage of paths, diffs, terminal scrollback, or secrets is a defect.

## Direct terminal (separate security boundary)

**Honest statement required in UI before first enable:**

> A terminal can run any command as your Windows user (or the remote gateway’s user). It is **not** limited to the folder you granted. Only turn this on if you understand that.

Constraints (design):

1. **Explicit opt-in** per profile (+ per host: Local vs named remote). Default **off**.
2. Optional constraints (Advanced): allowlist of shells, working directory default, block network tools — **best-effort**, never marketed as sandbox.
3. **Output redaction:** before “Add to chat” / send context, show preview with redaction pass (tokens, `AKIA…`, private key armor). User must confirm send.
4. Closing the drawer does not kill the session (upstream pattern); **explicit Close terminal** stops the PTY.
5. Remote terminal = shell on remote host; label host in the drawer title always.

## Remote vs local execution

| Action | Runs where |
|---|---|
| Chat / tools via gateway | Gateway host (local loopback or remote URL) |
| Folder grant file ops | Only if grant is on **this PC** and session is local **or** a future remote-fs protocol exists — do not pretend local Explorer paths are remote |
| Embedded terminal | Host chosen at opt-in |
| Git commit/push from UI | Working copy path shown in identity strip |

Identity strip must never imply local when remote (see UX-PLAN).

## Git actions

| Action | Confirmation | Auto? |
|---|---|---|
| Inspect diff | none (read); `--no-ext-diff` | — |
| Stage / unstage exact files | pathspec only; refuse secrets / `-` options / traversal; never `git add -A` | **Never** auto |
| Save checkpoint (commit) | message + confirm; exact paths; refuse unrelated staged index; local only | **Never** auto; **never** means pushed |
| Push to GitHub / remote | **implemented (CP12)** — network publish grant + exact remote confirm; disposable-remote tested | **Never** silent; never from Save checkpoint |
| Create PR / release | **implemented transport (CP12)** — after verified push; fixture/loopback tested; live auth setup-required | **Never** from commit alone; compare URL ≠ PR |
| Worktree create | separate destination folder consent; trusted base rev; no junction escape | **Never** auto |
| Worktree remove | confirm; dirty refused; no force by default; branch delete separate | **Forbidden** force default |
| Destructive reset / force push | — | **Forbidden** as default |

### Guided Git safety (implemented limits)

- Trusted `git` + `ArgumentList` only (no interpolated shell).
- Mutating ops use an **owned empty** `core.hooksPath` (not a shared predictable temp), `commit.gpgsign=false`, and pinned env; diffs use `--no-ext-diff`. **Global Git config/author are never rewritten.**
- Residual risk: clean/smudge **filters** in `.gitattributes` — matching paths are **refused** rather than executed blindly.
- Credential helpers are irrelevant for local commit (no network from Save checkpoint). Guided push refuses `!` helpers / transport command overrides and does not implicitly run OS helpers (HTTPS helper auth stays setup-required).
- Save checkpoint result UI must stay separate from Push and must not claim publication.
- **Push (CP12/12b):** separate **network publish grant** (default off). Folder grant / terminal / YOLO / Advanced do **not** authorise push. Preview shows exact branch, local SHA, remote name/**effective push URL**; confirm revalidates fingerprint; push + ls-remote + reconcile target that push URL (not fetch-only remote name); no force/delete/mirror/--all/--tags; refuse credentialed/username-only HTTP userinfo, query/fragment, ext/helper/`::`, UNC, untrusted command-executing config; `insteadOf` / `pushInsteadOf`, multi-pushurl; `GIT_TERMINAL_PROMPT=0`; durable uncertain gate + kill/drain on cancel/timeout.
- **PR (CP12/12b):** only after verified push to the exact push destination; no embedded PAT; setup-required when user-owned GitHub auth is unavailable; opening a compare URL is not a created PR.

Upstream Hermes Desktop offers Commit & Push and force worktree options — Hikyaku **recommends** keeping destructive and publish actions user-initiated and separate in Simple mode.

## Cloud sharing

- Only after user selects content + accepts “send to provider / share link” dialog.
- Re-check secret heuristics on the payload. For **credential leaves and suspected keys**, sharing stays **hard-blocked**; Acknowledge records understanding only and **does not share**.

## What this design refuses

- Whole-drive or “Documents” silent grant  
- Terminal implied by folder grant  
- Auto backups  
- Auto push / auto release  
- Claiming shell is confined to the folder  
- Perfect secret detection  

The early HTML prototype of these scenarios was removed once the app implemented them.
