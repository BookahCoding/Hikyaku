# Inbox results Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each Inbox result shows what the job found (the agent's final answer, or the script's output), with a status mark, unread marks and an in-place expander, plus a tiny read-only Hermes plugin that serves script-only output.

**Architecture:** Core gains `CronRunText` (run document → status + text), `JobRunDetails` (fetches one run's result: the run chat's last answer, or the plugin's output through `command.dispatch`) and `InboxReadStore` (read marks per PC). The App's `JobResultVM` gains the excerpt, status, unread and expander; `JobInboxUi` fetches details for rows on screen, three at a time, cached in memory. The plugin lives in `hermes-plugin/hikyaku-cron/`.

**Tech Stack:** C# / .NET 9, WPF, xUnit; Python 3.11+ with unittest for the plugin.

**Spec:** `docs/superpowers/specs/2026-10-28-inbox-results-design.md`

## Global Constraints

- Contract: hermes-agent `cron/scheduler.py`, `cron/scheduler_prompt.py` (`_job_doc_header`, `## Prompt` / `## Response` frames, `**Response Characters:** N`), `hermes_cli/web_routers/cron.py` (`_cron_output_runs_dir`, ids `cron_output:<job>:<run>`), plugin API `ctx.register_command(name, handler, description=, args_hint=)` (`hermes_cli/plugins.py`).
- Plugin command: `hikyaku-cron`, arg `output <profile> <job_id> <run>`; JSON answer `{"ok": true, "text": "...", "truncated": false}` or `{"ok": false, "error": "not_found" | "invalid" | "unreadable"}`; names `^[A-Za-z0-9_.-]{1,64}$` (profile, job), run `^[A-Za-z0-9_.-]{1,80}$`; ≤ 512 KB; read-only.
- Results are never written to disk by Hikyaku; read marks are (ids only). Nothing but reads goes to the gateway.
- Simple view shows no technical hint; the plugin hint is Advanced-only (`ExperienceFeature.InboxSetup`, already Advanced-only).
- Commits: explicit paths; Co-Authored-By trailer only. Gates after App tasks; protected hash and `.superpowers` manifest unchanged.

## Review Focus

1. A run document that is huge, empty or not in any known shape: the row shows something sensible ("No output") and never throws — test in Task 1.
2. A job whose run chat has no assistant message yet (still running, or failed before answering): the row keeps its preview and no "Ask about this" breaks — test in Task 2.
3. A plugin answer that is not JSON, or a plugin that times out: treated as absent, the preview stays — test in Task 2.
4. Scrolling a long "All results" list must not fire dozens of requests at once (at most three in flight) — test in Task 4.
5. Read marks for a gateway are not shown on another gateway with the same job ids — test in Task 3.

---

### Task 1: CronRunText

**Files:** Create `src/Hikyaku.Core/CronRunText.cs`; Test `tests/Hikyaku.Tests/Checkpoint49InboxResultsTests.cs`.

**Interfaces — Produces:** `public enum RunStatus { Ok, Failed, Silent }`; `public sealed record RunText(RunStatus Status, string Text, string? Reason)`; `public static class CronRunText { public static RunText Parse(string document); public static string Excerpt(string text, int maxLines = 3, int maxChars = 280); }`.

- [ ] Step 1: tests with the exact hermes shapes:
  - success script: `"# Cron Job: mail\n\n**Job ID:** abc\n**Run Time:** 2026-10-28T09:50:49\n**Mode:** no_agent (script)\n\n---\n\nThree new mails.\n"` → Ok, "Three new mails."
  - failed script: header + `"**Status:** script failed\n\nTraceback…"` → Failed, Reason "script failed", Text starts "Traceback".
  - silent: header + `"**Status:** silent (empty output)\n"` → Silent, Text "".
  - blocked: header + `"**Status:** BLOCKED\n\nThe assembled prompt …"` → Failed, Reason "BLOCKED".
  - agent doc: header + `"**Schedule:** every 1h\n\n**Prompt Characters:** 5\n## Prompt\n\nhello\n\n**Response Characters:** 11\n## Response\n\nAll is well\n"` → Ok, "All is well".
  - collapsed preview (whitespace folded, truncated with "…"): `"# Cron Job: shiwake-private-issue-monitor **Job ID:** 6516cca0727c **Run Time:** 2026-10-28 09:50:49 **Mode:** no_agent (script) --- Found 2 issues…"` → Ok, "Found 2 issues…".
  - junk: `""` → Silent ""; `"plain text"` → Ok "plain text"; 2 MB of text → no throw.
  - `Excerpt` keeps at most 3 non-empty lines and 280 chars, ending with "…" when cut.
- [ ] Steps 2–5: run (fail), implement (line-based header strip; `## Response` frame by length when the number parses, else to the end; `---` separator; `**Status:**` mapping; collapsed form handled by a regex over the folded header tokens), run (pass), commit.

### Task 2: JobRunDetails

**Files:** Create `src/Hikyaku.Core/JobRunDetails.cs`; Test as Task 1.

**Interfaces — Consumes:** `CronRunText` (Task 1); `SessionsApi.MessagesAsync(sessionId, profile, limit, offset, ct)` → `SessionMessagesPage.Messages` (`SessionMessage.Role`, `.Content`); `IJsonRpcPeer.RequestAsync("command.dispatch", {name, arg})` → `{type: "plugin", output: "<json>"}`, `RpcException` code `NotACommand` (see `Inbox.cs` `CronAsync`).
**Produces:** `public sealed record RunDetail(RunText Result, bool FromPlugin)`; `public sealed class JobRunDetails(Func<string, string?, int, CancellationToken, Task<IReadOnlyList<SessionMessage>>> messages, Func<IJsonRpcPeer?> rpc) { public bool? PluginPresent { get; } public Task<RunDetail?> LoadAsync(JobResult result, CancellationToken ct); }`.
- Agent run: last non-empty `assistant` message of the newest page (limit 50) → `RunText(Ok, text, null)`; none → null.
- Script run (`Run.Id` starts `cron_output:`): if `PluginPresent != false`, dispatch `output <profile> <job> <run>`; `ok:true` → `CronRunText.Parse(text)`, `FromPlugin=true`; not-a-command / non-JSON / timeout (5 s) → `PluginPresent=false` and fall back; `ok:false` → fall back. Fallback: `CronRunText.Parse(result.Preview)`.
- [ ] Step 1: tests with fakes: agent answer picked (last assistant, skipping tool/user and empty); no assistant → null; plugin ok; plugin not-a-command → remembered absent (second call does not dispatch); plugin non-JSON → absent; plugin `ok:false not_found` → preview fallback, presence unchanged.
- [ ] Steps 2–5.

### Task 3: InboxReadStore

**Files:** Modify `src/Hikyaku.Core/JobInbox.cs` (add the store beside `JobInboxPrefs`); Test as Task 1.

**Interfaces — Produces:** `public sealed class InboxReadStore(IPreferenceStore store) { Task LoadAsync(CancellationToken ct = default); bool IsRead(string gatewayId, string runKey); Task MarkReadAsync(string gatewayId, IEnumerable<string> runKeys, CancellationToken ct = default); const int Max = 5000; }` — JSON `{ "<gateway>": ["key", …] }`, newest kept when over `Max`.
- [ ] Step 1: tests: mark then read back after a reload; another gateway does not see it; over 5000 keeps the newest; corrupt file → empty.
- [ ] Steps 2–5.

### Task 4: Hermes plugin `hikyaku-cron`

**Files:** Create `hermes-plugin/hikyaku-cron/plugin.yaml`, `hermes-plugin/hikyaku-cron/__init__.py`, `hermes-plugin/hikyaku-cron/README.md`, `hermes-plugin/hikyaku-cron/tests/test_output.py`.

- `__init__.py`: `register(ctx)` → `ctx.register_command("hikyaku-cron", handle, description="Read one scheduled-job run's output for the Hikyaku Inbox (read-only)", args_hint="output <profile> <job_id> <run>")`; `handle(raw_args)` parses exactly four tokens, validates names, resolves the folder with `hermes_cli.web_routers.cron._cron_output_runs_dir(profile, job_id)` when importable (else `hermes_constants.get_hermes_home()/cron/output/<job>` and only for profile `default`), resolves `<run>.md` with `Path.resolve()` and checks it is inside the resolved folder (`is_relative_to`), refuses symlinks, reads ≤ 512 KB (`truncated` when longer), returns `json.dumps(...)`.
- [ ] Step 1: unittest (stdlib, no dependency; pytest also runs it): valid read; each invalid token (`..`, `/`, `\`, empty, 65 chars, spaces); symlink to outside → invalid; missing → not_found; 600 KB file → 512 KB + truncated; wrong verb → invalid.
- [ ] Step 2: run `python -m unittest discover -s hermes-plugin/hikyaku-cron/tests` (fail), implement, pass. Step 3: commit.

### Task 5: Inbox UI

**Files:** Modify `src/Hikyaku.App/JobInboxUi.cs`, `src/Hikyaku.App/MainWindow.xaml` (row template, header "Mark all read", unread counts, Advanced plugin hint), `src/Hikyaku.App/MainWindow.xaml.cs` (attach `JobRunDetails` and `InboxReadStore`), `src/Hikyaku.App/UiAcceptance.cs` (screenshot `08c-inbox-results.png`); Test `tests/Hikyaku.App.Tests/Checkpoint49InboxResultsWpfTests.cs`.

**Interfaces — Consumes:** Tasks 1–3. **Produces:** `JobResultVM`: `Excerpt`, `FullText` (Markdown), `StatusLabel`, `StatusVisibility`, `IsUnread`, `UnreadVisibility`, `IsExpanded`, `AskCommand` (agent runs only), `LoadProblem`; `MainViewModel.AttachJobRunDetails(JobRunDetails details, InboxReadStore reads)`, `MarkAllJobResultsReadCommand`, `JobInboxPluginHint`, `JobInboxPluginHintVisibility` (Advanced and `PluginPresent == false`).
- Fetch for the first 20 rows on show (and on expand) with a `SemaphoreSlim(3)`; in-memory cache by `JobResult.Key`; click toggles the expander and marks read; "Ask about this" calls the existing `OpenJobResultAsync`.
- [ ] Step 1: WPF tests with fake details/reads: excerpt and status shown; expand marks read and shows the full text; failed shows "Failed" with the reason; agent row has Ask, script row does not; at most 3 loads in flight for 40 rows; folder rows show their unread count and it drops when a result is read; hint only in Advanced when the plugin is absent.
- [ ] Steps 2–5 incl. gates and the screenshot check.

### Task 6: Docs, version 0.7.0, mutation checks

**Files:** `Directory.Build.props` (0.7.0), `CHANGELOG.md` (0.7.0 section above the PR bullets already in Unreleased), `docs/release-notes/v0.7.0.txt` (plain text incl. the merged PRs and the plugin install command), `README.md` (Inbox paragraph + plugin), `docs/HANDOFF.md`.
- [ ] Mutations (copy, restore, cmp): header not stripped; failed mapped to Ok; plugin presence not remembered; read marks not keyed by gateway; no concurrency limit — each must fail a test.
- [ ] Full gates; commit; ask the maintainer before push and release.
