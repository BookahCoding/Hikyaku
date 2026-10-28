"""Queue a folder-bridge call in memory. This process never opens a path."""
import json
import threading
import time
import uuid

LEASE_S = 10
_OPS = {"list", "read", "write", "delete_file", "delete_folder"}
_PLATFORMS = {"desktop", "tui"}
_lock = threading.Lock()
_queue = []


class _Item:
    def __init__(self, profile, op, path, text, data):
        self.id = uuid.uuid4().hex
        self.profile = profile
        self.op = op
        self.path = path
        self.text = text
        self.data = data
        self.claimed = False
        self.done = threading.Event()
        self.result = None
        self.lease = 0.0


def reset():
    with _lock:
        for item in _queue:
            item.result = {"ok": False, "error": "not_open"}
            item.done.set()
        _queue.clear()


def handle_tool(params, profile=None):
    params = params or {}
    op = params.get("op")
    if op not in _OPS or not profile:
        return json.dumps({"ok": False, "error": "invalid"})
    item = _Item(profile, op, params.get("path") or "", params.get("text"), params.get("data"))
    with _lock:
        item.lease = time.monotonic() + LEASE_S
        _queue.append(item)
    while not item.done.wait(0.05):
        with _lock:
            if item.done.is_set():
                break
            if time.monotonic() > item.lease:
                if item in _queue:
                    _queue.remove(item)
                return json.dumps({"ok": False, "error": "not_open"})
    return json.dumps(item.result if item.result is not None else {"ok": False, "error": "not_open"})


def handle_command(raw_args, platform=None):
    if platform not in _PLATFORMS:
        return json.dumps({"ok": False, "error": "invalid"})
    parts = (raw_args or "").split(" ", 2)
    verb = parts[0] if parts and parts[0] else ""
    if verb == "pending":
        return _pending()
    if verb == "hold" and len(parts) >= 2:
        return _hold(parts[1])
    if verb == "answer" and len(parts) == 3:
        return _answer(parts[1], parts[2])
    return json.dumps({"ok": False, "error": "invalid"})


def _pending():
    with _lock:
        for item in _queue:
            if not item.claimed and not item.done.is_set():
                item.claimed = True
                body = {"ok": True, "pending": True, "id": item.id, "profile": item.profile,
                        "op": item.op, "path": item.path}
                if item.text is not None:
                    body["text"] = item.text
                if item.data is not None:
                    body["data"] = item.data
                return json.dumps(body)
    return json.dumps({"ok": True, "pending": False})


def _hold(item_id):
    with _lock:
        item = _find(item_id)
        if item is None or not item.claimed or item.done.is_set():
            return json.dumps({"ok": False, "error": "invalid"})
        item.lease = time.monotonic() + LEASE_S
    return json.dumps({"ok": True})


def _answer(item_id, payload):
    try:
        parsed = json.loads(payload)
    except json.JSONDecodeError:
        return json.dumps({"ok": False, "error": "invalid"})
    if not isinstance(parsed, dict):
        return json.dumps({"ok": False, "error": "invalid"})
    with _lock:
        item = _find(item_id)
        if item is None:
            return json.dumps({"ok": False, "error": "invalid"})
        item.result = parsed
        if item in _queue:
            _queue.remove(item)
        item.done.set()
    return json.dumps({"ok": True})


def _find(item_id):
    return next((item for item in _queue if item.id == item_id), None)


def register(ctx):
    schema = {
        "name": "hikyaku_files",
        "description": "List, read, or write a file in a folder the user allowed in Hikyaku, or ask before deleting a file or folder. Files stay on the PC where Hikyaku is open.",
        "parameters": {
            "type": "object",
            "properties": {
                "op": {"type": "string", "enum": ["list", "read", "write", "delete_file", "delete_folder"]},
                "path": {"type": "string"},
                "text": {"type": "string"},
                "data": {"type": "string", "description": "Base64 file bytes for write."},
            },
            "required": ["op", "path"],
        },
    }

    def tool(params, **kwargs):
        return handle_tool(params, profile=kwargs.get("profile"))

    def command(raw_args, **kwargs):
        return handle_command(raw_args, platform=kwargs.get("platform"))

    ctx.register_tool(name="hikyaku_files", toolset="hikyaku_files", schema=schema, handler=tool,
                      description=schema["description"])
    ctx.register_command("hikyaku-files", command,
                         description="Let the open Hikyaku app answer a folder request.",
                         args_hint="pending | hold <id> | answer <id> <json>")
