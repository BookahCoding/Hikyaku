# 0.7.0 Inbox results — final review findings (to fix before release)

Reviewer: fresh agent, read-only, range `af5758a..97a6ec9`. Not yet acted on. Fix Critical and Important test-first
(each test fails without the fix; prove with a mutation), run the gates, then ask the maintainer before push/release.

## Critical
1. **Plugin timeout not treated as absent** — `src/Hikyaku.Core/JobRunDetails.cs` catch filter misses `RpcTimeoutException`
   (`JsonRpcClient.cs`, derives from `Exception`). It falls to the catch-all in `LoadRunDetailAsync`: "Could not load the result",
   nothing cached, re-asked on every rebuild, each script row holds a slot 5 s. Fix: catch it; add a test with a fake RPC throwing it.

## Important
2. **A run with no answer yet stays "No answer yet" for the session** — `JobInboxUi.cs` caches a `null` detail (a run still
   running) and the preview fallback taken while `rpc()` was null. Fix: do not cache `null` or a non-definite fallback.
3. **Rows after the first 20 stay "Loading…"** in "All results" until clicked. Fix: load when a row is realized, or show a
   plain preview instead of "Loading…" (the current test asserts no fetch on scroll; update it).
4. **Plugin has no caller check** — `hermes-plugin/hikyaku-cron/cron_output.py`: Hermes also dispatches plugin commands from
   messaging gateways (`gateway/run_inbound_hooks.py` → `get_plugin_command_handler`), so a Telegram/Discord user could read
   any profile's cron output. Fix: declare the dispatcher's `platform`/`source` keyword (`plugin_command_dispatch.
   invoke_plugin_command`) and refuse anything but the dashboard/TUI; confirm the value the dashboard WebSocket sends.
5. **"Plugin absent" is shared across gateways and set by any transient error** — one `JobRunDetails` lives for the window
   (`MainWindow.xaml.cs`); any `RpcException`/`InvalidOperationException` (e.g. `ObjectDisposedException` on reconnect) marks
   it absent. Fix: one `JobRunDetails` per connection; mark absent only on `NotACommand` (4018) or an unreadable answer.
6. **Real Hermes shapes misread** — `CronRunText.cs`: `**Status:** monitor source failed` → should be Failed; folded
   `no_change (agent run suppressed)` → Silent; the agent wake-gate doc ("Script gate returned `wakeAgent=false` — agent
   skipped") → Silent; `Error: …` config docs → Failed; an agent answer of `[SILENT]` / `NO_REPLY` (`JobRunDetails.cs`) →
   "Nothing new" (mirror hermes `_is_cron_silence_response`). Rule: a status containing "failed" is Failed.

## Minor (may defer; say so to the maintainer)
7. `SetDetail` does not raise `ExcerptVisibility` (opened-before-loaded row shows excerpt and full text).
8. Plugin paths: a symlinked job/parent folder is followed (require the resolved base inside resolved `<home>/cron/output`);
   check-then-open race (use `os.open(..., O_NOFOLLOW)` + `fstat` regular file); symlink loop `RuntimeError` on Python < 3.13
   uncaught; CI on Windows likely skips the symlink test.
9. Two quick `MarkReadAsync` calls race on `inbox-read.json.tmp`; `InboxReadStore.LoadAsync` can run twice on overlapping refreshes.
10. `_runDetailCache` keys lack the gateway; an unknown profile falls back to the launch home in `_cron_output_runs_dir`.
11. Plugin-read agent docs: the response frame counts code points vs UTF-16 `Substring` (emoji cut); unframed case should take
    the last `## Response`; the prompt frame is not skipped.
12. Up to 512 KB Markdown renders on the UI thread; `truncated` is ignored (say "cut at 512 KB").
13. First run marks every existing result unread (per spec, but noisy; consider marking older-than-today read once).

## Checked OK
Empty/huge/unknown documents never throw; read marks keyed per gateway; at most 3 fetches in flight, dictionaries touched on
the UI thread only; privacy (only ids on disk); plugin API (`register_command`, raw-string args, relative import, reply shape
`{"type":"plugin","output":str}`, handler exceptions → 4018); run-name regex rejects separators/ADS; folders, groups, hide.

## Declined to judge
Whether `hermes plugins install`'s plugin guard accepts the traversal strings in `tests/`; whether `MarkdownBlock` fetches
remote images (tracking pixels) from a result; the `platform` value the dashboard WebSocket passes; WPF test timing.
