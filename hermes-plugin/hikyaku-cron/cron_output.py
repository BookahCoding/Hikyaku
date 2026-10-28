"""hikyaku-cron: one read-only command for the Hikyaku Inbox.

``/hikyaku-cron output <profile> <job_id> <run>`` returns one scheduled run's output document
(``<profile home>/cron/output/<job_id>/<run>.md``), which the dashboard's run listing only previews in 180 characters. The
answer is JSON: ``{"ok": true, "text": ..., "truncated": false}`` or ``{"ok": false, "error": "invalid" | "not_found" |
"unreadable"}``. Nothing else is read and nothing is written.
"""
import json
import re
from pathlib import Path

COMMAND = "hikyaku-cron"
ARGS_HINT = "output <profile> <job_id> <run>"
LIMIT = 512 * 1024
_NAME = re.compile(r"[A-Za-z0-9_.-]{1,64}")
_RUN = re.compile(r"[A-Za-z0-9_.-]{1,80}")


def _valid(token, pattern):
    return bool(pattern.fullmatch(token)) and token.strip(".") != "" and ".." not in token


def _default_dir(profile, job_id):
    """The job's output folder, resolved the way the dashboard resolves it."""
    try:
        from hermes_cli.web_routers.cron import _cron_output_runs_dir
        return Path(_cron_output_runs_dir(profile, job_id))
    except ImportError:
        if profile != "default":
            raise LookupError(profile)
        from hermes_constants import get_hermes_home
        return Path(get_hermes_home()) / "cron" / "output" / job_id


def _answer(**fields):
    return json.dumps(fields)


# The dashboard WebSocket (tui_gateway command.dispatch with no session) passes platform "desktop"
# when HERMES_DESKTOP=1, otherwise "tui" (_resolve_session_platform). Messaging gateways pass their own
# platform (telegram, discord, …) via invoke_plugin_command. Anything else is refused.
_DASHBOARD_PLATFORMS = frozenset({"desktop", "tui"})


def handle(raw_args, resolve_dir=None, *, platform=None, source=None):
    # source is the dispatcher's session source (None from the dashboard, a SessionSource from a chat app).
    # The allow decision is the platform string, which the caller cannot choose.
    del source
    if platform not in _DASHBOARD_PLATFORMS:
        return _answer(ok=False, error="invalid")
    tokens = (raw_args or "").split()
    if len(tokens) != 4 or tokens[0] != "output":
        return _answer(ok=False, error="invalid")
    _, profile, job_id, run = tokens
    if not (_valid(profile, _NAME) and _valid(job_id, _NAME) and _valid(run, _RUN)):
        return _answer(ok=False, error="invalid")
    try:
        folder = (resolve_dir or _default_dir)(profile, job_id)
    except LookupError:
        return _answer(ok=False, error="not_found")
    path = Path(folder) / (run + ".md")
    try:
        if not Path(folder).is_dir():
            return _answer(ok=False, error="not_found")
        if path.is_symlink():
            return _answer(ok=False, error="invalid")
        base = Path(folder).resolve()
        target = path.resolve()
        if not target.is_relative_to(base):
            return _answer(ok=False, error="invalid")
        if not target.is_file():
            return _answer(ok=False, error="not_found")
        with open(target, "rb") as fh:
            data = fh.read(LIMIT + 1)
    except OSError:
        return _answer(ok=False, error="unreadable")
    truncated = len(data) > LIMIT
    return _answer(ok=True, text=data[:LIMIT].decode("utf-8", errors="replace"), truncated=truncated)


def register(ctx):
    ctx.register_command(COMMAND, handle,
                         description="Read one scheduled-job run's output for the Hikyaku Inbox (read-only)",
                         args_hint=ARGS_HINT)
