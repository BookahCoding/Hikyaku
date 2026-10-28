# Inbox results: design

Date: 2026-10-28. Release: 0.7.0. Status: approved in conversation by the maintainer (goals, Hermes side, delivery);
this spec records it for review before the implementation plan.

## Goal

The Inbox shows what each scheduled job found, so the maintainer can read results at a glance, spot problems fast and
follow up in a chat. Today a row's preview is the run document's header ("# Cron Job: … **Job ID:** … **Run Time:** …")
for script-only jobs and the job's own prompt (for example "/bookahmail-mail") for agent jobs.

## Decisions (maintainer, 2026-10-28)

- Goals: read results at a glance; spot problems fast; follow up in a chat. Not a copy of the Android Inbox.
- Script-only output needs a Hermes-side reader: a small read-only Hermes plugin, kept in this repository, installed once
  on the gateway (`hermes plugins install BookahCoding/Hikyaku/hermes-plugin/hikyaku-cron --enable`); it survives Hermes
  updates. Hikyaku works without it (cleaned short preview).
- The existing folders and groups stay as they are.

## What the person sees

- Each result row: job name, time, a status mark, and the first two or three lines of the result.
  - Agent job: the agent's final answer (the last assistant message of the run's chat).
  - Script-only job: the script's output, without the document header.
- Clicking a row expands the full result in place, rendered like a chat reply (Markdown, maths, code). Agent results have
  an "Ask about this" button that opens the run as a chat (today's behaviour of a click).
- Status: a failed run (script failed, blocked, agent error) has a red "Failed" mark and the reason line; a silent run
  (no output, `wakeAgent=false`) reads "Nothing new". Failed runs stay in the existing Failed folder.
- Unread: a result is unread until it is expanded or opened; unread rows are bold with a dot; folders and groups show unread
  counts; "Mark all read" marks the list on show. Read marks are kept per PC (like the groups).
- Without the plugin: script-only rows show the gateway's 180-character preview with the header removed; Advanced view
  shows one line under the list: "Install the hikyaku-cron plugin on the gateway to see whole script outputs" (with the
  command). Simple view shows no technical hint.

## Hermes plugin `hikyaku-cron` (hermes-plugin/hikyaku-cron/)

- `plugin.yaml` (manifest_version 2, name `hikyaku-cron`, no tools, no hooks) and `__init__.py` with `register(ctx)`
  calling `ctx.register_command("hikyaku-cron", handler, …)`.
- Command: `output <profile> <job_id> <run>` returns JSON `{"ok": true, "text": "...", "truncated": false}` or
  `{"ok": false, "error": "not_found" | "invalid" | "unreadable"}`.
- Path: `<profile home>/cron/output/<job_id>/<run>.md`, the profile home resolved by Hermes' own helper
  (`hermes_cli.web_routers.cron._cron_output_runs_dir`) when importable, otherwise `get_hermes_home()` for the default
  profile only.
- Safety: `profile` and `job_id` must match `^[A-Za-z0-9_.-]{1,64}$`, `run` must match `^[A-Za-z0-9_.-]{1,80}$`; the resolved
  path must stay inside the job's output folder (no traversal, no symlink escape); only `.md`; at most 512 KB read (then
  `truncated: true`); read-only; no other command.
- Tests: pytest for valid read, every invalid name form, traversal and symlink escape, missing file, the size cap.

## Hikyaku side

- Core `CronRunText`: from a run document (whole file or the collapsed 180-character preview) returns
  `(Status, Text)`: `## Response` section when present (honouring its `**Response Characters:** N` frame); else the text
  after the header's `---` line; else the text without the header lines (`# Cron Job:`, `**Job ID:**`, `**Run Time:**`,
  `**Mode:**`, `**Schedule:**`). `**Status:** script failed` / `BLOCKED` → Failed with the reason; `**Status:** silent` →
  Silent.
- Core `JobRunDetails`: for an agent run, the last non-empty assistant message of `GET api/sessions/{id}/messages?profile=`
  (existing `SessionsApi`); for a script run (`cron_output:<job>:<run>`), the plugin through `command.dispatch`
  `name=hikyaku-cron arg="output <profile> <job> <run>"` (the path the old herald-push Inbox used); a "not a command" answer
  means the plugin is absent (remembered per gateway for the session) and the cleaned preview is used.
- Fetching: only for rows on screen, at most three at a time, cached in memory for the session (results can hold private
  mail summaries, so nothing is written to disk); a finished run's result does not change, so a cached one is not fetched
  again.
- Read marks: `InboxReadStore` (JSON beside the groups store), keyed by gateway id and run id, bounded to the newest 5000.
- App: the row template gains the status mark, the excerpt, the unread dot and bold title, and an in-place expander with
  the rendered result and "Ask about this"; "Mark all read" in the Inbox header; unread counts on folders.

## Error handling

- A detail fetch that fails leaves the row with its cleaned preview and a small "Could not load the result" line
  (Advanced: the reason); nothing retries in a loop; the refresh button tries again.
- An unreadable plugin answer is treated as plugin absent.
- Nothing is ever sent to the gateway except reads.

## Testing

- Core: `CronRunText` against the exact document shapes written by hermes-agent `cron/scheduler.py` and
  `cron/scheduler_prompt.py` (success, script failed, silent, blocked, agent prompt/response with and without frames,
  the collapsed preview); `JobRunDetails` with fake session and command answers (plugin present, absent, failing);
  `InboxReadStore` (marks, bound, per gateway).
- WPF: rows show the excerpt and status; expanding marks read and shows the full text; "Ask about this" opens the chat;
  unread counts; the plugin hint only in Advanced.
- Plugin: pytest as above (run on the maintainer's gateway or any Python 3.11).

## Out of scope

Notifications changes; editing jobs; herald-push; an upstream Hermes change (may be offered later).
