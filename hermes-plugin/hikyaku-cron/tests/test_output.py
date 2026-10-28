"""The hikyaku-cron plugin's one command: read one scheduled run's output document, and nothing else."""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import cron_output  # noqa: E402


class ReadOutputTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.job_dir = self.root / "cron" / "output" / "j1"
        self.job_dir.mkdir(parents=True)
        (self.job_dir / "2026-10-28_09-50-49.md").write_bytes(b"# Cron Job: mail\n\n---\n\nThree new mails.\n")
        self.resolve = lambda profile, job_id: self.root / "cron" / "output" / job_id

    def tearDown(self):
        self.tmp.cleanup()

    def run_cmd(self, args, platform="desktop"):
        return json.loads(cron_output.handle(args, resolve_dir=self.resolve, platform=platform))

    def test_reads_one_runs_document(self):
        answer = self.run_cmd("output default j1 2026-10-28_09-50-49")
        self.assertEqual(answer, {"ok": True, "text": "# Cron Job: mail\n\n---\n\nThree new mails.\n", "truncated": False})

    def test_refuses_every_malformed_name_or_verb(self):
        for args in ["", "output", "output default j1", "read default j1 x", "output default j1 x extra",
                     "output default .. x", "output default j1 ..", "output default j1 ../j2/x", "output de/fault j1 x",
                     "output default j1 a\\b", "output default " + "j" * 65 + " x", "output default j1 " + "r" * 81,
                     "output default j 1 x", "output default j1 run.md/../x"]:
            with self.subTest(args=args):
                self.assertEqual(self.run_cmd(args), {"ok": False, "error": "invalid"})

    def test_a_missing_run_is_not_found(self):
        self.assertEqual(self.run_cmd("output default j1 2020-01-01_00-00-00"), {"ok": False, "error": "not_found"})
        self.assertEqual(self.run_cmd("output default nojob 2020-01-01_00-00-00"), {"ok": False, "error": "not_found"})

    def test_a_symlink_out_of_the_folder_is_refused(self):
        outside = self.root / "secret.md"
        outside.write_text("private", encoding="utf-8")
        try:
            os.symlink(outside, self.job_dir / "evil.md")
        except (OSError, NotImplementedError):
            self.skipTest("symlinks are not allowed here")
        self.assertEqual(self.run_cmd("output default j1 evil"), {"ok": False, "error": "invalid"})

    def test_a_long_document_is_cut_at_512_kb(self):
        (self.job_dir / "big.md").write_text("x" * 600_000, encoding="utf-8")
        answer = self.run_cmd("output default j1 big")
        self.assertTrue(answer["ok"])
        self.assertTrue(answer["truncated"])
        self.assertEqual(len(answer["text"].encode("utf-8")), 512 * 1024)

    def test_a_messaging_gateway_cannot_read_a_run(self):
        # The dashboard WebSocket passes platform "desktop" or "tui" (tui_gateway agent_factory
        # _resolve_session_platform). Messaging gateways pass their own platform (telegram, discord, …).
        for platform in ("telegram", "discord", "slack", "cli", "", None):
            with self.subTest(platform=platform):
                self.assertEqual(self.run_cmd("output default j1 2026-10-28_09-50-49", platform),
                                 {"ok": False, "error": "invalid"})
        self.assertEqual(self.run_cmd("output default j1 2026-10-28_09-50-49", "tui")["ok"], True)
        self.assertEqual(self.run_cmd("output default j1 2026-10-28_09-50-49", "desktop")["ok"], True)

    def test_register_adds_only_the_one_command(self):
        registered = []

        class Ctx:
            def register_command(self, name, handler, description="", args_hint=""):
                registered.append((name, args_hint))

        cron_output.register(Ctx())
        self.assertEqual(registered, [("hikyaku-cron", "output <profile> <job_id> <run>")])


if __name__ == "__main__":
    unittest.main()
