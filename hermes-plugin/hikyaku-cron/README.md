# hikyaku-cron (Hermes plugin)

Lets the Hikyaku Inbox show what a script-only scheduled job found. The dashboard lists such runs with only a
180-character preview of their output document; this plugin adds one read-only command that returns the document.

## Install (on the machine that runs your Hermes gateway)

```bash
hermes plugins install BookahCoding/Hikyaku/hermes-plugin/hikyaku-cron --enable
```

Restart the gateway afterwards. Hikyaku uses the plugin when it is there and falls back to the short preview when not.

## What it does

`/hikyaku-cron output <profile> <job_id> <run>` returns JSON:

- `{"ok": true, "text": "...", "truncated": false}` with the run's document (at most 512 KB; `truncated` when longer);
- `{"ok": false, "error": "invalid" | "not_found" | "unreadable"}` otherwise.

Profile and job names must match `[A-Za-z0-9_.-]{1,64}`, run names `[A-Za-z0-9_.-]{1,80}`; only `<run>.md` inside that
job's output folder is read (no `..`, no symlinks out of the folder). It registers no tools or hooks and writes nothing.

## Tests

```bash
python -m unittest discover -s hermes-plugin/hikyaku-cron/tests
```
