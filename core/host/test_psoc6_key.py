import json
import os
import socket
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from psoc6_key import (DRIVE_LOG, HostOnlyRelay, copy_new_exports, door2_command, door2_locked, drive_candidates, drive_check,
                       find_drive_folder, lamp_on, open_screen, read_state, resolve_drive_folder, stop_egress_command)
import psoc6_key
from unittest import mock


class OpenScreenTest(unittest.TestCase):
    """start.sh / start.ps1 が出した合言葉で、ブラウザが画面を開いて自動で入る。開くのは決まった形の住所だけ。"""

    def test_opens_only_the_sign_in_link_and_remembers_the_screen(self):
        with tempfile.TemporaryDirectory() as root, mock.patch.object(psoc6_key, "ROOT", Path(root)):
            (Path(root) / "Logs").mkdir()
            opened = []
            self.assertTrue(open_screen("http://192.168.1.20:8080", "/login#code=PVDP-B7CV", lambda url: opened.append(url) or True))
            self.assertEqual((Path(root) / "Logs" / ".robomori-screen").read_text(), "http://192.168.1.20:8080")
            seen = []
            for bad in ("", "/login?code=PVDP-B7CV", "/login#code=pvdp-b7cv", "/login#code=PVDP-B7CV&x=1", "javascript:alert(1)"):
                self.assertFalse(open_screen("http://127.0.0.1:8000", bad, lambda url: seen.append(url) or True), bad)
            self.assertEqual(seen, [])
            urls = []
            open_screen("http://127.0.0.1:8000", "/login#code=ABCD-2345", lambda url: urls.append(url) or True)
            self.assertEqual(urls, ["http://127.0.0.1:8000/login#code=ABCD-2345"])

    def test_no_browser_is_not_an_error(self):
        def broken(url):
            raise RuntimeError("ブラウザがない")
        with tempfile.TemporaryDirectory() as root, mock.patch.object(psoc6_key, "ROOT", Path(root)):
            self.assertFalse(open_screen("http://127.0.0.1:8000", "/login#code=ABCD-2345", broken))


class LampTest(unittest.TestCase):
    def test_open_door_is_solid(self):
        self.assertTrue(lamp_on({"door2": {"state": "external"}}, False, 0.0, 1.0))
        self.assertTrue(lamp_on(None, True, 0.0, 1.0))

    def test_failure_blinks_fast_then_pending_blinks_slow(self):
        fast = {lamp_on(None, False, 10.0, 8.0 + i * 0.2) for i in range(5)}
        self.assertEqual(fast, {True, False})
        pending = [lamp_on({"door2": {"state": "store", "pending": True}}, False, 0.0, 20.0 + i * 0.25) for i in range(8)]
        self.assertEqual(pending, [True, True, False, False, True, True, False, False])

    def test_nothing_or_unreachable_core_is_off(self):
        self.assertFalse(lamp_on({"door2": {"state": "store", "pending": False}}, False, 0.0, 5.0))
        self.assertFalse(lamp_on(None, False, 0.0, 5.0))


class CommandTest(unittest.TestCase):
    def test_runs_only_the_door2_service(self):
        command = door2_command("人が PSoC 6 の SW2 を押した", dry_run=True)
        self.assertEqual(command[:2], ["docker", "compose"])
        self.assertIn("--rm", command)
        self.assertIn("DOOR2_DRY_RUN=1", command)
        self.assertEqual(command[-1], "door2")

    def test_closes_the_exit_after_door2(self):
        command = stop_egress_command()
        self.assertEqual(command[-1], "egress")
        self.assertIn("--stop", command)


class TimetableTest(unittest.TestCase):
    def test_locked_only_when_the_core_says_so(self):
        self.assertEqual(door2_locked({"schedule": {"door2_allowed": False, "label": "店内（営業中）"}}), "店内（営業中）")
        self.assertIsNone(door2_locked({"schedule": {"door2_allowed": True, "label": "店内・共有可"}}))
        self.assertIsNone(door2_locked(None))          # 中核に届かないときは door2 自身が時間割を確かめる


class DriveCopyTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.source = Path(self.tmp.name) / "export"
        self.target = Path(self.tmp.name) / "drive"
        self.source.mkdir()
        self.target.mkdir()
        self.secret = Path(self.tmp.name) / "secret.txt"
        self.secret.write_text("PC のほかのファイル")

    def tearDown(self):
        self.tmp.cleanup()

    def test_copies_new_summaries_once(self):
        (self.source / "guardian-records-20261025-120000.md").write_text("# 要約")
        (self.source / "guardian-records-20261025-120000.jsonl").write_text("{}\n")
        (self.source / ".guardian-records-20261025-120500.md.tmp").write_text("書きかけ")
        (self.source / "notes.txt").write_text("ほかのもの")
        done: set[str] = set()
        self.assertEqual(copy_new_exports(self.source, self.target, done),
                         ["guardian-records-20261025-120000.jsonl", "guardian-records-20261025-120000.md"])
        self.assertEqual(sorted(p.name for p in self.target.iterdir()),
                         ["guardian-records-20261025-120000.jsonl", "guardian-records-20261025-120000.md"])
        self.assertEqual(copy_new_exports(self.source, self.target, done), [])

    @unittest.skipIf(os.name == "nt", "Windows ではリンクを作るのに権限が要る")
    def test_never_follows_links(self):
        (self.source / "guardian-records-20261025-130000.md").symlink_to(self.secret)
        self.assertEqual(copy_new_exports(self.source, self.target, set()), [])
        self.assertEqual(list(self.target.iterdir()), [])

    def test_missing_folder_is_quiet(self):
        self.assertEqual(copy_new_exports(self.source / "nothing", self.target, set()), [])

    def test_each_copy_is_logged_for_the_screen(self):
        (self.source / "guardian-records-20261025-120000.md").write_text("# 要約")
        copy_new_exports(self.source, self.target, set())
        entries = [json.loads(line) for line in (self.source / DRIVE_LOG).read_text(encoding="utf-8").splitlines()]
        self.assertEqual([(e["kind"], e["file"], e["ok"]) for e in entries], [("copy", "guardian-records-20261025-120000.md", True)])
        self.assertEqual(entries[0]["to"], str(self.target))
        self.assertNotIn(DRIVE_LOG, [p.name for p in self.target.iterdir()])   # 記録そのものは Drive へ写さない


class DriveFolderTest(unittest.TestCase):
    def test_windows_looks_at_the_drive_letter_and_the_mirror(self):
        found = drive_candidates(Path("C:/Users/user"), "Windows")
        self.assertEqual(found[:2], ["G:\\マイドライブ", "G:\\My Drive"])
        self.assertIn(str(Path("C:/Users/user") / "マイドライブ"), found)

    def test_mac_finds_my_drive_and_makes_the_robomori_folder(self):
        with tempfile.TemporaryDirectory() as name:
            home = Path(name)
            self.assertIsNone(find_drive_folder(home, "Darwin"))           # Drive for desktop がない
            root = home / "Library" / "CloudStorage" / "GoogleDrive-user@example.com" / "マイドライブ"
            root.mkdir(parents=True)
            folder = find_drive_folder(home, "Darwin")
            self.assertEqual(folder, root / "robomori")
            self.assertTrue(folder.is_dir())

    def test_explicit_folder_and_check_file(self):
        with tempfile.TemporaryDirectory() as name:
            folder, note = resolve_drive_folder(str(Path(name) / "robomori"))
            self.assertEqual((folder, note), (Path(name) / "robomori", ""))
            path = drive_check(folder)
            self.assertTrue(path.name.startswith("robomori-drive-check-"))
            self.assertEqual([p.name for p in folder.iterdir()], [path.name])   # 書きかけは残らない
            self.assertEqual(resolve_drive_folder(None), (None, ""))
            self.assertIsNone(resolve_drive_folder(str(Path(name) / "no" / "such"))[0])


class _Answer(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.headers.get("X-Robomori-Key") != "right":
            self.send_response(403)
            self.end_headers()
            return
        body = json.dumps({"door2": {"state": "store", "pending": False}, "path": self.path}).encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


class CoreAccessTest(unittest.TestCase):
    def setUp(self):
        self.core = ThreadingHTTPServer(("127.0.0.1", 0), _Answer)
        self.addCleanup(self.core.server_close)
        threading.Thread(target=self.core.serve_forever, daemon=True).start()
        self.addCleanup(self.core.shutdown)
        self.port = self.core.server_address[1]

    def test_state_needs_the_start_key(self):
        url = f"http://127.0.0.1:{self.port}/api/key"
        self.assertEqual(read_state(url, "right")[0]["door2"]["state"], "store")
        self.assertEqual(read_state(url, "wrong"), (None, "key"))
        self.assertEqual(read_state("http://127.0.0.1:1/api/key", "right"), (None, "down"))

    def ask(self, port):
        data = b""
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=2) as connection:
                connection.settimeout(2)
                connection.sendall(b"GET /x HTTP/1.0\r\nX-Robomori-Key: right\r\n\r\n")
                while chunk := connection.recv(4096):
                    data += chunk
        except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
            # Refusal can reset connect, send or recv. Connection refused, timeouts
            # and unrelated IO errors must still fail the test.
            pass
        return data

    def test_relay_read_handles_disconnect_during_connect(self):
        for error in (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
            with self.subTest(error=error.__name__):
                with mock.patch.object(socket, "create_connection", side_effect=error("peer closed during connect")):
                    self.assertEqual(self.ask(self.port), b"")

    def test_relay_read_handles_disconnect_during_send(self):
        for error in (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
            with self.subTest(error=error.__name__):
                connection = mock.MagicMock()
                connection.sendall.side_effect = error("peer closed before send")
                with mock.patch.object(socket, "create_connection") as connect:
                    connect.return_value.__enter__.return_value = connection
                    self.assertEqual(self.ask(self.port), b"")
                connection.recv.assert_not_called()

    def test_relay_read_handles_disconnect_during_receive(self):
        for error in (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
            with self.subTest(error=error.__name__):
                connection = mock.MagicMock()
                connection.recv.side_effect = [b"partial response", error("peer closed during receive")]
                with mock.patch.object(socket, "create_connection") as connect:
                    connect.return_value.__enter__.return_value = connection
                    self.assertEqual(self.ask(self.port), b"partial response")
                connection.sendall.assert_called_once()

    def test_relay_read_does_not_hide_timeouts_or_unexpected_errors(self):
        for phase in ("connect", "sendall", "recv"):
            for error in (TimeoutError, OSError, ConnectionRefusedError):
                with self.subTest(phase=phase, error=error.__name__):
                    connection = mock.MagicMock()
                    with mock.patch.object(socket, "create_connection") as connect:
                        connect.return_value.__enter__.return_value = connection
                        if phase == "connect":
                            connect.side_effect = error("not a peer disconnect")
                        else:
                            getattr(connection, phase).side_effect = error("not a peer disconnect")
                        with self.assertRaises(error):
                            self.ask(self.port)

    def test_relay_serves_this_pc_and_cuts_other_pcs(self):
        relay = HostOnlyRelay("127.0.0.1", 0, self.port)
        self.addCleanup(relay.stop)
        port = relay.server.getsockname()[1]
        self.assertIn(b'"path": "/x"', self.ask(port))
        self.assertEqual((relay.served, relay.refused), (1, 0))
        relay.allow = lambda host: False          # ほかの PC から来たのと同じ
        self.assertEqual(self.ask(port), b"")
        self.assertEqual(relay.refused, 1)
        self.assertEqual(relay.last_refused, "127.0.0.1")

    def test_relay_needs_an_ipv4_address(self):
        with self.assertRaises(ValueError):
            HostOnlyRelay("::1", 0, self.port)


if __name__ == "__main__":
    unittest.main()
