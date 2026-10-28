# Implementation stages — after design approval (plan only)

**Status:** sequenced plan. Checkpoint 7 delivered shell themes, native approvals, MIT/credits (no push).  
**Final review (when broader app work is done):** Claude Code `claude-opus-5-5` effort **high**, findings-first, **no auto-fixes**.  
**Baseline after CP7:** see `docs/HANDOFF.md` (101/101 tests; toast **Unavailable** still honest).

## Hard dependencies (do not reorder casually)

```
Live login acceptance ──► toast Observed gate ──► folder grants
        │                        │
        ├─ PKCE / auth hardening ┤
        │                        ▼
        │                 Git Changes (read) ──► commit ──► push
        │                        │
        │                        └─ worktree
        ▼
   Bots / voice / HUD (parallel after chat trust)
Terminal (after permissions UX approved; not blocked by Git)
Updater (after private/public auth design sign-off)
License/credits file edits (only in an approved publication stage)
```

## Stage 0 — Design gate (this checkpoint)

| Acceptance | Evidence |
|---|---|
| Parity inventory, UX plan, permissions, stages, synthetic prototype committed | docs (the HTML prototype was removed once the app replaced it) |
| The maintainer's review of proposed choices in UX-PLAN / PERMISSIONS | human sign-off |
| No app code / no push / protecteds untouched | git + hashes |

## Stage 1 — Trust the pipe (live acceptance)

| Work | Acceptance tests |
|---|---|
| Real user login guide (password path) | USER-ACCEPTANCE expanded: user completes login, send, receive, Inbox once on their gateway |
| Fix toast **Unavailable** where environment allows | UI acceptance labels **Observed** or documents OS limitation without claiming success |
| Auth/PKCE Windows browser flow | Fixture + one live PKCE sign-in; tickets never in URL |
| Source/history security scan before any publication | `check-secrets` clean; no private history in artifacts |

**Depends on:** user-owned gateway; no server installs from client.

## Stage 2 — Project Files / Changes (local grant) — **complete (CP9)**

| Work | Acceptance |
|---|---|
| Explicit folder grant + revoke | Deny outside root; junction escape tests |
| Projects nav Files / Preview / Changes (read) | Synthetic + fixture; no shell |
| Secret heuristic warnings on share/stage | Honest limits copy shown (stage not implemented) |
| Identity strip host/path/branch | Local vs remote labels correct |

**Depends on:** Stage 1 chat trust; PERMISSIONS-DESIGN approval.

## Stage 3 — Git checkpoint vs publish — **partial (CP10)**

| Work | Acceptance |
|---|---|
| Diff inspect | Matches working tree; read-only scopes OK |
| Save checkpoint (commit) | **complete** — separate control; local only; exact files; no network |
| Push to GitHub / Create PR | **complete transport (CP12/12b)** — network grant + effective push-URL pin + fail-closed untrusted config/hooks/URLs + durable uncertain gate; bare-remote + fixture/loopback; live GitHub auth setup-required; no silent push |
| Worktree “separate working copy” | **complete** — create/list/remove; dirty refuse; no force default |
| Conflict UI | Refuse checkpoint when conflict codes present; no auto-resolve |

**Depends on:** Stage 2 grant on a real git repo path. Stage 4 terminal is **not** gated on toast or unfinished cosmetic work.

## Stage 4 — Terminal opt-in — **complete (CP11 + CP11b corrections)**

| Work | Acceptance |
|---|---|
| Opt-in copy + per-profile enable | **Done** — default off; warning ack; gateway+profile+Local Windows |
| Bottom drawer PTY (local) | **Done** — ConPTY; persist while hidden; explicit Close kills |
| Add-to-chat with redaction consent | **Done** — preview + destination revalidation; keys hard-blocked |
| Remote terminal | **Unavailable** with truthful copy (no silent local substitute) |

**Depends on:** PERMISSIONS approval (settled); **not** on folder grant.

## Stage 5 — Bots / Inbox depth / subagents

| Work | Acceptance |
|---|---|
| Bots roster / rooms when protocol ready | **CP8 fixture-tested** (live smoke still pending) |
| Subagent Steer/Stop frame | **CP8 fixture-tested** — Steer ack ≠ child-read claim |
| Needs-you badge placement per UX-PLAN | Discoverable without Advanced |

**Depends on:** Stage 1; gateway capabilities.

## Stage 6 — Advanced surfaces & Simple mode

| Work | Acceptance |
|---|---|
| Simple/Advanced toggle (visibility only) | Permissions unchanged under both |
| Skills/MCP/messaging/cron admin as Advanced | Gated by protocol availability |
| Keyboard / reduced-motion / high-contrast | Checklist in UI acceptance |

## Stage 7 — Updater & packaging trust

| Work | Acceptance |
|---|---|
| Implement [`UPDATES.md`](UPDATES.md) | **Done (15c, fixture-tested):** public check; project-key signature verify (a same-source hash is not authenticity); no silent install; install disabled until a public key is embedded |
| Private release auth | `gh` / DPAPI PAT — **no** embedded token |
| Signing trust story | Documented; optional Authenticode later |

**Depends on:** publication decision; still **no push** until the maintainer authorizes.

## Stage 8 — License & public credits (publication stage only)

**Do not add/update LICENSE or credits in the research checkpoint.**

When the maintainer authorizes publication staging:

| Item | Content |
|---|---|
| MIT license + public credit | BookahCoding; contact exactly as in `docs/CREDITS.md` (**explicitly approved public exception**) |
| Upstream notices | Hermes / Herald / fonts / third-party — keep scanners strict elsewhere |
| Final review | Claude Code **claude-opus-5-5** effort **high**, findings-first, no auto fixes |

## Out of scope across stages (unless reopened)

- Phone plugins / wake word / Wear  
- Claiming Electron Hermes Desktop binary/plugin compatibility  
- Auto backups, silent push, folder-as-shell  
- Editing `verify-hermes.ps1` or `.superpowers/`  
- Spawning other coding agents for implementation without a new checkpoint
