# UX plan — approachable full desktop (design)

**Status:** design recommendation for the maintainer's review.  
**Grounded in:** [`DESKTOP-PARITY.md`](DESKTOP-PARITY.md), NN/g progressive disclosure, GitHub Desktop guided commit/push split, Hermes Desktop Simple/Advanced **visibility** pattern (docs 2026-10-25).  
**Not:** app implementation. Mock: `design/index.html`.

## Intent (agreed constraints)

- Keep a **familiar ChatGPT-like flow** (sidebar → conversation → composer) while keeping a **genuine Hikyaku / Herald companion identity** (Ukiyo-e tokens, “Herald companion”, not an admin console).
- **Beginner can discover useful review actions without Advanced.** Advanced reveals technical/admin surfaces; it **does not** change permission policy.
- Local PC vs remote gateway stay obvious: persistent **host / path / branch** identity when a project is granted.
- Terminal permission is **separate** from folder grants (see PERMISSIONS-DESIGN).
- Motion: polished iOS/macOS-quality presence (subtle hierarchy), with **keyboard**, **high contrast**, and **reduced-motion** alternatives — not imitation OS chrome.

## Information architecture (recommended default)

### Primary nav (always visible)

| Item | Role |
|---|---|
| **Chats** | Default home. Session list, search, new chat. |
| **Bots** | Roster / rooms when implemented; empty state with honest “coming soon / needs gateway support” until then. |
| **Inbox** | Followed cron / herald-push outputs only — **not** approvals. |
| **Scheduled** | Gateway cron jobs (Hermes Desktop's Cron page), Advanced view only. Separate from Inbox, which shows followed run outputs. |
| **Capabilities** | Skills, toolsets and MCP servers for the active profile. Visible in Simple too (as in Hermes Desktop). |
| **Insights** | Usage analytics for the active profile. Visible in Simple too. |
| **Projects** | Granted folders + Git identity; empty until first explicit grant. |
| **Settings** | Connection, profiles, appearance, updates, Advanced toggle. |

### Contextual (not permanent primary destinations)

| Item | Placement |
|---|---|
| **Needs-you** | **Badge** on Chats (and Bots rooms later) + optional overflow “Waiting for you” sheet. Not a fifth forever-tab that competes with Chat for beginners. Count still visible. |
| **Files / Preview / Changes** | **Right panel** when a Project (or project-linked chat) is active. Tabs: Files · Preview · Changes. Hidden when no project. |
| **Terminal** | **Bottom drawer**, closed by default. Opens via Project → “Open terminal…” or Advanced shortcut. Never implied by a folder grant alone. |

### Advanced toggle (Settings → Appearance / Experience)

When **off (Simple — default for new installs after this design ships):**

- Hide: raw tool-call dumps, MCP/skills admin, memory graph, multi-gateway registry deep panels, YOLO, plugin manager, status-bar meters beyond connection.
- **Keep visible:** Chats, send/cancel, model picker (basic), attachments, Inbox, Needs-you badge, Projects grant, **Changes** (diff + Save checkpoint + Push as separate actions), conflict/secret warnings, Settings connection/profiles.

When **on (Advanced):**

- Reveal technical surfaces and power controls without granting new filesystem/shell rights.
- Layout memory separate from Simple (Hermes Desktop pattern: mode remembers its own arrangement).

**NN/g alignment:** frequent beginner needs stay on the first surface; Advanced is the secondary disclosure with clear scent (“Show advanced tools”).

## Local vs remote identity chrome

Always show a compact identity strip when connected:

`Profile · Gateway host · [Local folder path or Remote workspace] · Branch`

- **Local:** path is a user-granted folder on this PC.
- **Remote:** label **Remote gateway** + server host; path is the gateway’s workspace (not this PC’s home). Never silently resolve `~` against the Windows machine when the session is remote (upstream Desktop docs already warn this for artifacts).

## Git review placement (beginner-friendly)

Inspired by GitHub Desktop’s Changes → Commit → Push origin split:

1. **Changes** panel lists files + diff (read-first).
2. **Save checkpoint** = local commit. Label in UI: “Save checkpoint” with subtitle “Creates a local Git commit. Does not upload.”
3. **Push to GitHub** = separate button, disabled until there is something to push; confirmation names remote + branch.
4. **Create separate working copy** = worktree; wizard language, not “worktree” alone in Simple mode (Advanced can show the Git term).

No combined “Commit & Push” as the only primary action in Simple mode (upstream Hermes offers Commit & Push — Hikyaku recommendation: split for learnability; Advanced may offer a combined shortcut later).

## Motion & accessibility

| Requirement | Design |
|---|---|
| Motion | 2–3 intentional transitions (panel open, Needs-you pulse, drawer rise); respect `prefers-reduced-motion` → instant show/hide |
| Keyboard | Tab order: nav → identity → main → panel → composer; Esc closes drawers/dialogs; documented shortcuts in Settings |
| High contrast | Token overrides; borders not color-only for Needs-you / errors |
| Narrow window | Collapse right panel behind a “Project” button; bottom terminal becomes full-width sheet |

## Scenarios the prototype must teach

See `design/` — synthetic only:

1. Explicit folder grant  
2. Inspect diff  
3. Save checkpoint ≠ Push  
4. Create separate working copy  
5. Conflict + unsafe secret warning  
6. Approval / Needs-you  
7. Disconnected / loading / empty / error  
8. Selected-profile hiding  

## Implementation status (Stage 20)
- **Advanced toggle** shipped as a visibility choice only (Settings → Experience, the palette, and an install-aware default). Simple hides the Reasoning and Fast chips, the YOLO control and raw tool-activity lines. **A risk already in effect is never hidden:** a chat with YOLO on keeps its indicator (and can still be turned off) in Simple. Approvals, folder grants, terminal and network permissions are identical in both views. The "new installs start Simple, existing installs keep the full chrome" default answers open choice 1 without surprising anyone after an update.
- **Command palette** (Ctrl+K) shipped; it offers navigation and reversible view actions only.
- Still design-only: per-mode layout memory, Needs-you as a sidebar row, Projects reveal-after-first-grant, the "Separate working copy" wording in Simple.

## Open choices for the maintainer

1. Default **Simple** vs keep current power-user chrome until first run wizard asks?  
2. Needs-you as badge-only vs badge + sidebar row when count > 0?  
3. Projects as primary nav from day one vs reveal after first grant?  
4. Worktree wording: “Separate working copy” only, or show Git term in Simple too?
