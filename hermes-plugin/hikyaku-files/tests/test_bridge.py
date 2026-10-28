"""The hikyaku-files plugin queues a request and does not touch the disk."""
import json
import os
import sys
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import bridge  # noqa: E402


class BridgeQueueTests(unittest.TestCase):
    def setUp(self):
        bridge.reset()
        self._lease = bridge.LEASE_S
        bridge.LEASE_S = 10

    def tearDown(self):
        bridge.LEASE_S = self._lease
        bridge.reset()

    def test_pending_is_empty_until_a_tool_call_is_waiting(self):
        self.assertEqual(json.loads(bridge.handle_command("pending", platform="desktop")),
                         {"ok": True, "pending": False})

    def test_an_answer_unblocks_only_the_call_it_names(self):
        results = {}

        def run(path):
            results[path] = json.loads(bridge.handle_tool({"op": "read", "path": path}, profile="default"))

        first = threading.Thread(target=run, args=("a",))
        second = threading.Thread(target=run, args=("b",))
        first.start()
        claimed = self._wait_pending()
        second.start()
        self.assertEqual(claimed["path"], "a")
        self.assertEqual(claimed["profile"], "default")
        self.assertEqual(claimed["op"], "read")
        bridge.handle_command(
            "answer " + claimed["id"] + ' {"ok": true, "encoding": "utf-8", "text": "one"}',
            platform="tui")
        first.join(2)
        self.assertEqual(results["a"]["text"], "one")
        other = self._wait_pending()
        self.assertEqual(other["path"], "b")
        self.assertNotEqual(other["id"], claimed["id"])
        bridge.handle_command('answer ' + other["id"] + ' {"ok": true, "text": "two"}', platform="desktop")
        second.join(2)
        self.assertEqual(results["b"]["text"], "two")

    def test_a_claimed_call_expires_without_a_hold(self):
        bridge.LEASE_S = 0.2
        result = {}

        def run():
            result["v"] = json.loads(bridge.handle_tool({"op": "read", "path": "a"}, profile="default"))

        threading.Thread(target=run).start()
        self._wait_pending()
        time.sleep(0.4)
        self.assertEqual(result["v"], {"ok": False, "error": "not_open"})

    def test_hold_of_an_unknown_id_is_invalid(self):
        self.assertEqual(json.loads(bridge.handle_command("hold missing", platform="desktop")),
                         {"ok": False, "error": "invalid"})

    def test_a_phone_platform_cannot_claim_and_desktop_can(self):
        ready = threading.Event()

        def run():
            ready.set()
            bridge.handle_tool({"op": "read", "path": "a"}, profile="default")

        threading.Thread(target=run, daemon=True).start()
        ready.wait(2)
        time.sleep(0.05)
        refused = json.loads(bridge.handle_command("pending", platform="telegram"))
        self.assertEqual(refused, {"ok": False, "error": "invalid"})
        claimed = json.loads(bridge.handle_command("pending", platform="desktop"))
        self.assertTrue(claimed["pending"])
        bridge.handle_command('answer ' + claimed["id"] + ' {"ok": true}', platform="desktop")

    def test_the_tool_does_not_touch_the_disk(self):
        with patch("builtins.open") as opened, patch("pathlib.Path.read_bytes") as read_bytes, patch("os.remove") as remove:
            result = {}

            def run():
                result["v"] = bridge.handle_tool({"op": "read", "path": "a"}, profile="default")

            threading.Thread(target=run).start()
            claimed = self._wait_pending()
            bridge.handle_command('answer ' + claimed["id"] + ' {"ok": true, "text": "x"}', platform="desktop")
            for _ in range(50):
                if "v" in result:
                    break
                time.sleep(0.02)
            opened.assert_not_called()
            read_bytes.assert_not_called()
            remove.assert_not_called()
            self.assertIn("ok", result["v"])

    def test_a_shell_op_is_invalid_and_queues_nothing(self):
        self.assertEqual(json.loads(bridge.handle_tool({"op": "shell", "path": "a"})),
                         {"ok": False, "error": "invalid"})
        self.assertEqual(json.loads(bridge.handle_command("pending", platform="desktop")),
                         {"ok": True, "pending": False})

    def _wait_pending(self):
        for _ in range(50):
            answer = json.loads(bridge.handle_command("pending", platform="desktop"))
            if answer.get("pending"):
                return answer
            time.sleep(0.02)
        self.fail("no request became pending")


if __name__ == "__main__":
    unittest.main()
