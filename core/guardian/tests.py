import io
import json
import os
import tempfile
from pathlib import Path
from unittest import mock

from datetime import datetime, time, timedelta

from django.core.management import CommandError, call_command
from django.db import connection
from django.test import Client, TestCase
from django.test.utils import CaptureQueriesContext
from django.utils import timezone

import layout
from monitor import Monitor
from protocol import decode, parse_telemetry

from . import auth, drive, schedule
from .core_loop import CoreHub, CoreLoop, Recorder, say_to_log
from .models import Event, Gate, Live, Opinion, Pass, RelayTrust, Request, ShareSetting, Slot
from .relay import RelayTail
from .sim import StandIn


def robot(rid, x=-4.2, z=0.0, vibration=0.1, **extra):
    item = {"id": rid, "x": x, "z": z, "speed": 0.5, "condition": "Normal", "stopped": False, "held": False, "task": "",
            "phase": "", "carrying": False, "vibration": vibration, "speed_scale": 1.0, "wait": ""}
    item.update(extra)
    return item


def telemetry(seq, robots, boot="unity-test", **extra):
    return json.dumps({"v": 1, "type": "telemetry", "boot": boot, "door": "store", "seq": seq, "t": seq * 0.5,
                       "robots": robots, "people": [], "approved": [], "declined": [], "restored": [], **extra}).encode()


def relay_line(kind, wall, **fields):
    return json.dumps({"wall": wall, "mono": 0, "kind": kind, **fields}, ensure_ascii=False) + "\n"


class RelayTailTest(TestCase):
    def test_reads_only_new_complete_lines_and_follows_new_files(self):
        with tempfile.TemporaryDirectory() as name:
            folder = Path(name)
            first = folder / "20261024-100000.jsonl"
            first.write_text(relay_line("start", "2026-10-24T10:00:00") + '{"kind": "sam', encoding="utf-8")
            tail = RelayTail(folder)
            records, switched = tail.poll()
            self.assertTrue(switched)
            self.assertEqual([r["kind"] for r in records], ["start"])     # 書きかけの行はまだ読まない
            with first.open("a", encoding="utf-8") as file:
                file.write('ple", "wall": "2026-10-24T10:00:01"}\nnot json\n')
            records, switched = tail.poll()
            self.assertFalse(switched)
            self.assertEqual([r["kind"] for r in records], ["sample", "unreadable"])
            (folder / "20261024-110000.jsonl").write_text(relay_line("start", "2026-10-24T11:00:00"), encoding="utf-8")
            records, switched = tail.poll()
            self.assertTrue(switched)
            self.assertEqual(tail.path.name, "20261024-110000.jsonl")

    def test_gap_uses_the_relay_clock(self):
        tail = RelayTail(Path("."))
        self.assertIsNone(tail.gap({"wall": "2026-10-24T10:00:00"}))
        self.assertEqual(tail.gap({"wall": "2026-10-24T10:00:04.500"}), 4.5)
        self.assertIsNone(tail.gap({"wall": "broken"}))


class CoreLoopTest(TestCase):
    def setUp(self):
        self.sent = []
        self.online = True
        self.hub = CoreHub(Monitor(learn_seconds=0), log=say_to_log)
        self.loop = CoreLoop(self.hub, self.sent.append, lambda: self.online)

    def test_telemetry_becomes_live_state_and_advice(self):
        self.loop.on_event("unity_connected", "127.0.0.1:5000", 0.0)
        self.loop.on_event("line", telemetry(1, [robot("A"), robot("C", stopped=True, x=3.6, z=-3.4)]), 0.1)
        self.loop.tick(0.2)
        live = Live.get()
        self.assertTrue(live.unity_connected)
        self.assertEqual([r["id"] for r in live.robots], ["A", "C"])
        self.assertEqual(live.dispatch[0]["robot"], "C")                 # 止まった C に推薦が出る
        advice = json.loads(self.sent[-1])
        self.assertEqual(advice["type"], "advice")
        self.assertTrue(Event.objects.filter(source="dispatch").exists())

    def test_scene_from_unity_is_kept_once_and_conversations_are_logged(self):
        scene = {"v": 1, "type": "scene", "boot": "unity-test", "seq": 1, "west": -9.74, "east": 9.74, "south": -6.04, "north": 6.04,
                 "things": [{"id": "Dining Table 1", "kind": "table", "label": "テーブル1", "x": 6.1, "z": -3.4, "w": 1.9, "d": 1.3}],
                 "spots": [{"id": "home-A", "kind": "home", "label": "A 待機", "x": -4.2, "z": -5.25}]}
        self.loop.on_event("line", json.dumps(scene, ensure_ascii=False).encode("utf-8"), 0.0)
        talk = {"robot": "A", "question": "今の状況は？", "answer": "スープを運んでいます", "by": "あなた", "n": 1}
        self.loop.on_event("line", telemetry(1, [robot("A", says="スープを運んでいます", table=1)], seats=[{"id": "Chair 1-1-0", "state": "occupied", "table": 1}],
                                             talk=talk), 0.1)
        self.loop.tick(0.2)
        live = Live.get()
        self.assertEqual(live.scene["things"][0]["label"], "テーブル1")
        self.assertEqual((live.scene["boot"], live.scene_rev), ("unity-test", 1))
        self.assertEqual(live.robots[0]["says"], "スープを運んでいます")
        self.assertEqual(live.seats, [{"id": "Chair 1-1-0", "state": "occupied", "table": 1}])
        said = Event.objects.get(source="talk")
        self.assertEqual(said.data["answer"], "スープを運んでいます")
        Live.objects.filter(pk=1).update(scene={})
        self.loop.tick(1.0)                     # 配置が変わらなければ書き直さない
        self.assertEqual(Live.get().scene, {})

    def test_unity_side_never_touches_the_database(self):
        self.loop.tick(0.0)
        with CaptureQueriesContext(connection) as queries:
            self.loop.on_event("unity_connected", "127.0.0.1:5000", 0.0)
            for step in range(1, 20):                                     # 止まったロボットの推薦も含めて
                self.loop.on_event("line", telemetry(step, [robot("A"), robot("C", stopped=True, x=3.6, z=-3.4)]), step * 0.5)
                self.loop.fast_tick(step * 0.5)
            self.loop.on_event("unity_lost", None, 10.0)
        self.assertEqual(len(queries), 0)
        self.assertGreater(len([line for line in self.sent if b'"advice"' in line]), 5)
        self.loop.slow_tick(11.0)                                          # 記録は記録の側がまとめて書く
        self.assertTrue(Event.objects.filter(source="dispatch").exists())
        self.assertTrue(Event.objects.filter(source="door", text__contains="切れた").exists())

    def test_recorder_counts_what_it_could_not_keep(self):
        recorder = Recorder(limit=3)
        for i in range(5):
            recorder.event("hub", "log", f"行 {i}")
        self.assertEqual(recorder.flush(), 4)                              # 3 件と「2 件を書けなかった」
        self.assertTrue(Event.objects.filter(kind="dropped", text__contains="2 件").exists())

    def test_gate_opened_by_door2_reaches_unity(self):
        self.loop.on_event("line", telemetry(1, [robot("A")]), 0.0)
        Gate.objects.filter(pk=1).update(state="external")
        self.loop.tick(1.0)
        self.assertEqual(json.loads(self.sent[-1])["gate"], "external")

    def test_drill_from_the_screen_sends_three_unusable_lines(self):
        self.loop.on_event("line", telemetry(1, [robot("A")]), 0.0)
        self.loop.tick(0.0)
        Request.objects.create(kind="drill")
        before = len(self.sent)
        for step in range(1, 30):
            self.loop.tick(1.0 + step * 0.1)
        drill = [line for line in self.sent[before:] if b'"speed_scale":1.5' in line or b'"seq":\n' in line or line.endswith(b'"seq":\n')]
        self.assertGreaterEqual(len(drill), 2)
        self.assertTrue(Request.objects.get().done_at)
        self.online = False
        self.loop.on_event("unity_lost", None, 4.0)
        self.loop.tick(10.0)
        self.assertEqual(Live.get().door1, "standalone")

    def test_drill_without_unity_is_skipped(self):
        self.online = False
        Request.objects.create(kind="drill")
        self.loop.tick(0.0)
        self.assertTrue(Event.objects.filter(source="drill", kind="skipped").exists())

    def test_relay_records_become_trust_state_without_touching_the_relay(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name) / "20261024-100000.jsonl"
            path.write_text(relay_line("start", "2026-10-24T10:00:00", robot="B", threshold_mg=300)
                            + relay_line("sample", "2026-10-24T10:00:01", rms_mg=120.0), encoding="utf-8")
            loop = CoreLoop(self.hub, self.sent.append, lambda: True, RelayTail(Path(name)))
            loop.tick(0.0)                                    # 起動前の記録はまとめて 1 件
            self.assertTrue(Event.objects.filter(source="relay", kind="replay").exists())
            self.assertIsNone(RelayTrust.get().last_sample_seen_at)
            with path.open("a", encoding="utf-8") as file:
                file.write(relay_line("sample", "2026-10-24T10:00:06", rms_mg=410.0)
                           + relay_line("level", "2026-10-24T10:00:06", level="Warning", hold=False, reason="300 mg 以上が 2 回")
                           + "garbage\n")
            loop.tick(2.0)
            trust = RelayTrust.get()
            self.assertEqual((trust.samples, trust.gaps, trust.bad_lines, trust.last_level), (2, 1, 1, "Warning"))
            self.assertIsNotNone(trust.last_sample_seen_at)
            self.assertTrue(Event.objects.filter(source="relay", kind="gap").exists())
            self.assertTrue(Event.objects.filter(source="relay", kind="level").exists())
            # 中継の対象ロボットとしきい値、起動後に読んだ揺れの最大（分ごと）を覚える。起動前の 120 mg は入れない
            self.assertEqual((trust.robot, trust.threshold_mg), ("B", 300))
            self.assertEqual([peak for _, peak in trust.peaks], [410])
            self.assertEqual(trust.recent, [["10:00:06", 410]])           # 画面のグラフ（中継の時刻と mg）
            with path.open("a", encoding="utf-8") as file:
                file.write(relay_line("unity_disconnected", "2026-10-24T10:00:07", peer=None))
            loop.tick(4.0)
            self.assertEqual(Event.objects.get(source="relay", kind="unity_disconnected").text, "Unity と中継が切れた")
            body = signed_in().get("/api/state").json()["relay"]
            self.assertEqual((body["robot"], body["threshold_mg"], body["recent"], body["peak_mg"]), ("B", 300, [["10:00:06", 410]], 410))
            self.assertEqual(body["events"][0]["kind"], "unity_disconnected")


def signed_in(client=None):
    """PC 側で合言葉を出し、それで入った画面（テスト用）。"""
    client = client or Client()
    code, _ = auth.issue()
    response = client.post("/login", {"code": code})
    assert response.status_code == 302, response.status_code
    return client


class ViewsTest(TestCase):
    def test_dashboard_has_a_strict_content_security_policy(self):
        client = signed_in()
        response = client.get("/", HTTP_HOST="127.0.0.1:8000")
        self.assertEqual(response.status_code, 200)
        self.assertIn("script-src 'self'", response["Content-Security-Policy"])
        self.assertEqual(response["X-Frame-Options"], "DENY")
        self.assertEqual(response["Cache-Control"], "no-store")
        self.assertEqual(response["Cross-Origin-Opener-Policy"], "same-origin")
        # http の IPv4 の住所では付けない（ブラウザが使わず、F12 に誤りとして出すだけ）
        with self.settings(ALLOWED_HOSTS=["192.168.1.20"]):
            self.assertNotIn("Cross-Origin-Opener-Policy", client.get("/", HTTP_HOST="192.168.1.20:8000"))

    def test_state_lists_the_bands(self):
        body = signed_in().get("/api/state").json()
        for key in ("unity", "door1", "door2", "ai", "robots", "relay", "opinions", "events", "talk", "seats", "scene"):
            self.assertIn(key, body)
        self.assertEqual(body["door2"]["state"], "store")
        self.assertEqual(body["relay"]["state"], "none")
        self.assertEqual(body["scene"]["source"], "copy")

    def test_drill_needs_the_page_token(self):
        client = Client(enforce_csrf_checks=True)
        form = client.get("/login").content.decode()
        token = form.split('name="csrfmiddlewaretoken" value="')[1].split('"')[0]
        code, _ = auth.issue()
        self.assertEqual(client.post("/login", {"code": code, "csrfmiddlewaretoken": token}).status_code, 302)
        self.assertEqual(client.post("/api/drill").status_code, 403)
        page = client.get("/").content.decode()
        token = page.split('name="csrf-token" content="')[1].split('"')[0]
        self.assertEqual(client.post("/api/drill", HTTP_X_CSRFTOKEN=token).status_code, 202)
        self.assertEqual(client.post("/api/drill", HTTP_X_CSRFTOKEN=token).status_code, 409)   # 前の訓練が終わるまで
        self.assertEqual(client.get("/api/drill").status_code, 405)

    def test_layout_without_unity_is_the_copy_of_the_unity_floor(self):
        body = signed_in().get("/api/layout").json()
        self.assertEqual(body["source"], "copy")
        self.assertEqual(body["room"], {"west": -9.74, "east": 9.74, "south": -6.04, "north": 6.04})
        tables = [t for t in body["things"] if t["kind"] == "table"]
        self.assertEqual([t["x"] for t in tables], [6.1, 6.1, 6.1])                   # テーブルは東の壁ぎわ
        self.assertIn({"id": "service-table1", "kind": "service", "label": "テーブル1", "x": 3.6, "z": -3.4}, body["spots"])

    def test_layout_follows_the_scene_unity_sent(self):
        scene = {"boot": "unity-x", "seq": 1, "room": {"west": -5.0, "east": 5.0, "south": -4.0, "north": 4.0},
                 "things": [{"id": "Dining Table 1", "kind": "table", "label": "テーブル1", "x": 1.0, "z": 0.0, "w": 2.0, "d": 1.0}], "spots": []}
        Live.objects.update_or_create(pk=1, defaults={"unity_boot": "unity-x", "scene": scene, "scene_rev": 3})
        client = signed_in()
        body = client.get("/api/layout").json()
        self.assertEqual((body["source"], body["rev"]), ("unity", 3))
        self.assertEqual(body["things"][0]["x"], 1.0)
        self.assertEqual(client.get("/api/state").json()["scene"], {"source": "unity", "rev": 3, "boot": "unity-x"})
        Live.objects.filter(pk=1).update(unity_boot="unity-y")    # 別の起動の配置は使わない
        self.assertEqual(client.get("/api/layout").json()["source"], "copy")

    def test_unknown_host_is_refused(self):
        self.assertEqual(Client().get("/", HTTP_HOST="evil.example").status_code, 400)
        self.assertEqual(Client().get("/login", HTTP_HOST="evil.example").status_code, 400)


class PassTest(TestCase):
    def test_pages_data_and_scripts_need_the_one_time_password(self):
        client = Client()
        self.assertEqual(client.get("/").status_code, 302)
        self.assertEqual(client.get("/")["Location"], "/login")
        self.assertEqual(client.get("/api/state").status_code, 401)
        self.assertEqual(client.get("/api/layout").status_code, 401)
        self.assertEqual(client.get("/api/drive").status_code, 401)
        self.assertEqual(client.get("/static/guardian/dashboard.js").status_code, 302)   # 画面のスクリプトも入ってから
        self.assertEqual(client.get("/admin/").status_code, 302)
        self.assertEqual(client.get("/healthz").status_code, 200)
        self.assertEqual(client.get("/login").status_code, 200)

    def test_login_page_never_holds_the_password(self):
        code, _ = auth.issue()
        page = Client().get("/login").content.decode()
        self.assertNotIn(code, page)
        self.assertNotIn(auth.display(code), page)
        stored = Pass.objects.get()
        self.assertNotIn(code, stored.digest + stored.salt)    # DB にはハッシュだけ

    def test_the_sign_in_script_is_reachable_before_signing_in(self):
        # PC 側が開く /login#code=… を読んで送るスクリプト。# の後ろはサーバーに届かないので、合言葉はここに来ない
        page = Client().get("/login").content.decode()
        self.assertIn("guardian/login.js", page)
        self.assertIn("/static/guardian/login.js", auth.OPEN_PATHS)
        self.assertNotEqual(Client().get("/static/guardian/dashboard.js").status_code, 200)

    def test_a_password_works_once(self):
        code, _ = auth.issue()
        self.assertEqual(Client().post("/login", {"code": auth.display(code).lower()}).status_code, 302)   # 小文字・ハイフンも可
        self.assertEqual(Client().post("/login", {"code": code}).status_code, 401)

    def test_a_new_password_cancels_the_old_one(self):
        old, _ = auth.issue()
        new, _ = auth.issue()
        self.assertEqual(Client().post("/login", {"code": old}).status_code, 401)
        self.assertEqual(Client().post("/login", {"code": new}).status_code, 302)

    def test_five_wrong_tries_burn_the_password(self):
        code, _ = auth.issue()
        with mock.patch.object(auth, "FAIL_DELAY", 0):
            for _ in range(auth.TRIES):
                self.assertEqual(Client().post("/login", {"code": "WRONGPWD"}).status_code, 401)
            self.assertEqual(Client().post("/login", {"code": code}).status_code, 401)

    def test_an_expired_password_is_refused(self):
        code, item = auth.issue()
        Pass.objects.filter(pk=item.pk).update(expires_at=timezone.now() - timedelta(seconds=1))
        with mock.patch.object(auth, "FAIL_DELAY", 0):
            self.assertEqual(Client().post("/login", {"code": code}).status_code, 401)

    def test_after_login_only_paths_inside_the_screen(self):
        for target in ("//evil.example/x", "https://evil.example/", "/login", "javascript:alert(1)"):
            code, _ = auth.issue()
            response = Client().post("/login", {"code": code, "next": target})
            self.assertEqual(response["Location"], "/", target)

    def test_logout_needs_a_new_password(self):
        client = signed_in()
        self.assertEqual(client.get("/api/state").status_code, 200)
        self.assertEqual(client.post("/logout").status_code, 302)
        self.assertEqual(client.get("/api/state").status_code, 401)

    def test_issue_command_prints_the_password_and_can_close_every_screen(self):
        client = signed_in()
        out = io.StringIO()
        call_command("issue_otp", "--plain", "--logout-all", stdout=out)
        code = out.getvalue().strip()
        self.assertRegex(code, r"^[A-Z2-9]{4}-[A-Z2-9]{4}$")
        self.assertEqual(client.get("/api/state").status_code, 401)   # 入っていた画面も入り直し
        self.assertEqual(Client().post("/login", {"code": code}).status_code, 302)

    def test_the_pc_key_reads_only_its_own_state_with_the_host_key(self):
        key = "k" * 40
        with mock.patch.dict(os.environ, {"ROBOMORI_HOST_KEY": key}):
            self.assertEqual(Client().get("/api/key").status_code, 403)
            self.assertEqual(Client().get("/api/key", HTTP_X_ROBOMORI_KEY="wrong").status_code, 403)
            body = Client().get("/api/key", HTTP_X_ROBOMORI_KEY=key).json()
            self.assertEqual(set(body), {"door2", "schedule", "unity", "relay"})   # 鍵とランプと点検に要るものだけ
            self.assertEqual(body["door2"]["state"], "store")
            self.assertIn("door2_allowed", body["schedule"])
        with mock.patch.dict(os.environ, {"ROBOMORI_HOST_KEY": ""}):
            self.assertEqual(Client().get("/api/key", HTTP_X_ROBOMORI_KEY="").status_code, 403)


class DriveTest(TestCase):
    def setUp(self):
        self.folder = Path(tempfile.mkdtemp())
        self.patch = mock.patch.dict(os.environ, {"EXPORT_DIR": str(self.folder)})
        self.patch.start()
        self.addCleanup(self.patch.stop)

    def write(self, stamp, door2, records=2):
        lines = [json.dumps({"at": "2026-10-25T10:00:00", "kind": "ai", "text": f"記録 {i}"}, ensure_ascii=False) for i in range(records)]
        lines.append(json.dumps({"kind": "door2", **door2}, ensure_ascii=False))
        (self.folder / f"guardian-records-{stamp}.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8")
        (self.folder / f"guardian-records-{stamp}.md").write_text(f"# ロボもり 扉2 の記録 {stamp}\n\n本文 <script>x</script>\n", encoding="utf-8")

    def log(self, *entries):
        with (self.folder / ".drive-log.jsonl").open("a", encoding="utf-8") as file:
            for entry in entries:
                file.write(json.dumps(entry, ensure_ascii=False) + "\n")

    def test_inbox_pairs_files_with_the_pc_copy_log(self):
        self.write("20261025-090000", {"scheduled": True, "text": ""})
        self.write("20261025-100000", {"scheduled": False, "text": "共有文", "post": {"url": "https://bsky.app/x"}})
        self.write("20261025-110000", {"scheduled": False, "text": "", "errors": ["Gemini に届かなかった"]})
        to = "G:\\マイドライブ\\robomori"
        self.log({"at": "2026-10-25T09:00:05", "kind": "drive", "ok": True, "to": to},
                 {"at": "2026-10-25T09:00:06", "kind": "copy", "file": "guardian-records-20261025-090000.md", "ok": True, "to": to},
                 {"at": "2026-10-25T09:00:06", "kind": "copy", "file": "guardian-records-20261025-090000.jsonl", "ok": True, "to": to},
                 {"at": "2026-10-25T10:00:06", "kind": "copy", "file": "guardian-records-20261025-100000.md", "ok": False, "error": "使用中"},
                 "not an object")
        (self.folder / "notes.txt").write_text("ほかのファイルは並べない", encoding="utf-8")
        body = signed_in().get("/api/drive").json()
        self.assertEqual([i["id"] for i in body["items"]], ["20261025-110000", "20261025-100000", "20261025-090000"])
        kinds = {i["id"]: (i["kind"], i["synced"]) for i in body["items"]}
        self.assertEqual(kinds["20261025-090000"], ("scheduled", "sent"))
        self.assertEqual(kinds["20261025-100000"], ("shared", "failed"))
        self.assertEqual(kinds["20261025-110000"], ("error", "waiting"))
        self.assertEqual(body["counts"], {"all": 3, "sent": 1, "waiting": 1, "failed": 1})
        self.assertEqual(body["drive"]["to"], to)
        self.assertTrue(body["drive"]["found"])
        self.assertEqual(body["log"][0]["file"], "guardian-records-20261025-100000.md")   # 新しい順

    def test_one_item_is_text_and_only_by_its_stamp(self):
        self.write("20261025-100000", {"scheduled": False, "text": "共有文", "judgement": {"assessment": "段差", "recommendation": "点検"}})
        client = signed_in()
        item = client.get("/api/drive/20261025-100000").json()
        self.assertIn("<script>x</script>", item["text"])     # 文字として返す（画面は textContent で入れる）
        self.assertEqual(item["judgement"]["assessment"], "段差")
        for bad in ("..%2Fsecret", "20261025", "x" * 15, "20261025-10000a"):
            self.assertEqual(client.get(f"/api/drive/{bad}").status_code, 404, bad)

    def test_links_are_not_followed(self):
        target = self.folder / "outside.md"
        target.write_text("外のファイル", encoding="utf-8")
        try:
            (self.folder / "guardian-records-20261025-120000.md").symlink_to(target)
        except OSError:
            self.skipTest("この環境ではリンクを作れない")
        self.assertEqual(drive.listing()["items"], [])
        self.assertIsNone(drive.message("20261025-120000"))

    def test_missing_folder_is_an_empty_inbox(self):
        with mock.patch.dict(os.environ, {"EXPORT_DIR": str(self.folder / "none")}):
            body = drive.listing()
        self.assertEqual(body["counts"]["all"], 0)
        self.assertFalse(body["drive"]["found"])


class FakeGemini:
    def judge(self, situation):
        assert "通路中" in situation
        return {"assessment": "通路中の床に段差がある可能性", "recommendation": "段差を確かめ、テープで示す", "confidence": "中"}


class FakeBluesky:
    """店のアカウント（did:plc:test）。返信は did:plc:other から 1 件。店は did:plc:other をフォローしている。"""

    def __init__(self, follows=("did:plc:other",)):
        self.posted, self.records, self.follows_asked = [], [], 0
        self._follows = set(follows)

    def post(self, text, facets=None):
        self.posted.append(text)
        return {"uri": "at://did:plc:test/app.bsky.feed.post/1", "cid": "bafypost", "url": "https://bsky.app/profile/test/post/1",
                "handle": "robomori.test", "created_at": timezone.now().isoformat()}

    def create_record(self, collection, record):
        self.records.append((collection, record))
        n = len(self.records)
        return {"uri": f"at://did:plc:test/{collection}/{n}", "cid": f"bafyrecord{n}"}

    def replies(self, uri):
        now = timezone.now().isoformat()
        return [{"uri": "at://did:plc:other/app.bsky.feed.post/9", "cid": "bafyreply9", "did": "did:plc:other",
                 "url": "https://bsky.app/profile/x/post/9", "handle": "x.test",
                 "text": "# 見出し [link](javascript:alert(1))", "created_at": now, "indexed_at": now}]

    def follows(self, actor):
        self.follows_asked += 1
        assert actor == "did:plc:test"
        return set(self._follows)

    def resolve_handle(self, handle):
        return {"x.test": "did:plc:other"}[handle]


class Door2CommandTest(TestCase):
    def setUp(self):
        Live.objects.update_or_create(pk=1, defaults={
            "robots": [{"id": "A", "condition": "Normal", "stopped": False}, {"id": "C", "condition": "Normal", "stopped": True}],
            "slowdowns": [{"kind": "area", "name": "通路中", "scale": 0.6, "reason": "通路中でAとBが揺れた"}],
            "dispatch": [{"robot": "C", "situation": "停止（保持）", "person": "保守担当", "eta_s": 11.7, "backup": "管理者"}]})
        Event.objects.create(source="ai", kind="ai", text="場所の問題: 通路中 の速度を 0.6 倍に下げた")
        ShareSetting.objects.create(pk=1, share="post", gemini=True)   # ここでは公開の投稿の流れを確かめる（既定は会社だけ）

    def run_door2(self, *args, clients=(None, None)):
        with tempfile.TemporaryDirectory() as drive, \
                mock.patch.dict(os.environ, {"EXPORT_DIR": drive, "DOOR2_MIN_OPEN_SECONDS": "0"}), \
                mock.patch("guardian.management.commands.door2.make_clients", return_value=clients):
            call_command("door2", *args, stdout=open(os.devnull, "w"))
            return sorted(p.name for p in Path(drive).iterdir()), "".join(p.read_text(encoding="utf-8") for p in Path(drive).glob("*.md"))

    def test_dry_run_opens_writes_and_closes(self):
        files, report = self.run_door2("--by", "テスト")
        self.assertEqual(len(files), 2)
        self.assertIn("通路中", report)
        gate = Gate.get()
        self.assertEqual(gate.state, "store")
        self.assertIsNotNone(gate.last_closed_at)
        self.assertIn("Gemini には聞いていない", gate.last_result["summary"])
        self.assertEqual(Opinion.objects.count(), 0)

    def test_outside_opinions_are_kept_as_proposals_and_replies_are_not_taken_twice(self):
        bluesky = FakeBluesky()
        self.run_door2(clients=(FakeGemini(), bluesky))
        self.assertEqual(Opinion.objects.get(source="gemini").text, "通路中の床に段差がある可能性")
        self.assertEqual(Opinion.objects.filter(source="post").count(), 1)
        Event.objects.create(source="ai", kind="ai", text="もう一度")
        files, report = self.run_door2(clients=(FakeGemini(), bluesky))
        self.assertEqual(Opinion.objects.filter(source="reply").count(), 1)   # 2 回目に返信を取り込む
        self.run_door2(clients=(FakeGemini(), bluesky))
        self.assertEqual(Opinion.objects.filter(source="reply").count(), 1)   # 同じ返信は 2 度取らない

    def test_store_record_is_portable_atproto_data_without_floats(self):
        bluesky = FakeBluesky()
        self.run_door2(clients=(None, bluesky))
        collection, record = bluesky.records[0]
        self.assertEqual(collection, "internal.robomori.report")
        self.assertEqual(record["slowdowns"][0]["speedPercent"], 60)
        self.assertEqual(record["responders"][0]["etaSeconds"], 12)
        self.assertEqual(record["post"], {"uri": "at://did:plc:test/app.bsky.feed.post/1", "cid": "bafypost"})

        def no_floats(value):
            if isinstance(value, dict):
                return all(no_floats(v) for v in value.values())
            if isinstance(value, list):
                return all(no_floats(v) for v in value)
            return not isinstance(value, float)

        self.assertTrue(no_floats(record))                       # AT プロトコルのレコードに小数は入れられない
        self.assertEqual(Opinion.objects.get(source="record").uri, "at://did:plc:test/internal.robomori.report/1")
        # ルール AI の判断（店の基準の版つき）も、記録に入る
        self.assertEqual(len(record["judgement"]["rulesVersion"]), 12)
        self.assertIn(record["judgement"]["store"], ("safe", "caution", "danger"))

    def test_timetable_keeps_door2_shut_during_business_hours(self):
        Slot.objects.create(start=time(0, 0), end=time(0, 0), mode="store")   # 一日中 営業中
        with self.assertRaises(SystemExit) as stopped:
            self.run_door2(clients=(FakeGemini(), FakeBluesky()))
        self.assertEqual(stopped.exception.code, 3)
        self.assertEqual(Gate.get().state, "store")
        self.assertTrue(Event.objects.filter(kind="refused").exists())

    def test_reply_text_is_not_markdown_in_the_report(self):
        bluesky = FakeBluesky()
        self.run_door2(clients=(None, bluesky))
        _, report = self.run_door2(clients=(None, bluesky))
        self.assertIn("    # 見出し [link](javascript:alert(1))", report)   # 字下げしたコードブロックの中

    def test_already_open_gate_is_left_alone(self):
        Gate.objects.update_or_create(pk=1, defaults={"state": "external"})
        self.run_door2()
        self.assertEqual(Gate.get().state, "external")

    def again(self, bluesky, gemini=None):
        Event.objects.create(source="ai", kind="ai", text="もう一度")
        return self.run_door2(clients=(gemini, bluesky))

    def test_share_off_sends_nothing_and_gemini_can_be_left_out(self):
        ShareSetting.objects.filter(pk=1).update(share="off", gemini=False)

        class Never:
            def judge(self, situation):
                raise AssertionError("聞いてはいけない")

        bluesky = FakeBluesky()
        _, report = self.run_door2(clients=(Never(), bluesky))
        self.assertEqual((bluesky.posted, bluesky.records), ([], []))
        self.assertIn("共有しない", report)
        self.assertTrue(Event.objects.filter(kind="share_off").exists())
        self.assertFalse(Opinion.objects.filter(source__in=("post", "record", "gemini")).exists())

    def test_replies_from_people_the_store_did_not_choose_leave_no_text(self):
        bluesky = FakeBluesky()
        self.run_door2(clients=(None, bluesky))
        ShareSetting.objects.filter(pk=1).update(accept="listed", allow=["did:plc:friend"])
        _, report = self.again(bluesky)
        self.assertFalse(Opinion.objects.filter(source="reply").exists())
        self.assertNotIn("見出し", report)
        self.assertTrue(Event.objects.filter(kind="reply_skipped").exists())
        ShareSetting.objects.filter(pk=1).update(allow=["x.test"])   # ハンドルで指定しても、DID で見分ける
        self.again(bluesky)
        self.assertEqual(Opinion.objects.get(source="reply").detail["did"], "did:plc:other")

    def test_records_chain_and_point_at_the_replies_in_their_authors_repos(self):
        bluesky = FakeBluesky()
        self.run_door2(clients=(None, bluesky))                        # 記録 1（返信はまだない）
        ShareSetting.objects.filter(pk=1).update(share="off")
        self.again(bluesky)                                             # 返信を受け取るが、記録は書かない
        reply = Opinion.objects.get(source="reply")
        self.assertEqual(reply.detail["in_record"], "")
        self.assertEqual(len(bluesky.records), 1)
        ShareSetting.objects.filter(pk=1).update(share="record")
        self.again(bluesky)                                             # 記録 2: 持ち越した意見を参照する。タイムラインには流さない
        first, second = bluesky.records[0][1], bluesky.records[1][1]
        self.assertNotIn("previous", first)
        self.assertEqual(second["previous"], {"uri": "at://did:plc:test/internal.robomori.report/1", "cid": "bafyrecord1"})
        self.assertEqual(second["opinions"], [{"author": "did:plc:other", "post": {"uri": "at://did:plc:other/app.bsky.feed.post/9", "cid": "bafyreply9"}}])
        self.assertEqual((second["share"], second["accept"]), ("record", "following"))
        self.assertNotIn("post", second)
        self.assertEqual(len(bluesky.posted), 1)
        self.assertNotIn("見出し", json.dumps(second, ensure_ascii=False), "返信の文はコピーしない（その人のリポジトリを指す）")
        reply.refresh_from_db()
        self.assertEqual(reply.detail["in_record"], "at://did:plc:test/internal.robomori.report/2")
        self.assertEqual(Opinion.objects.get(uri__endswith="report/2").detail["share"], "record")
        self.again(bluesky)
        self.assertNotIn("opinions", bluesky.records[2][1], "一度載せた意見は次の記録に持ち越さない")


class ShareSettingTest(TestCase):
    """扉 2 で何を出し、誰の返信を受け取るかを、店が画面から選ぶ。"""

    def setUp(self):
        Live.objects.update_or_create(pk=1, defaults={"robots": [{"id": "C", "condition": "Normal", "stopped": True}],
                                                      "slowdowns": [], "dispatch": []})

    def test_needs_the_password(self):
        self.assertEqual(Client().get("/api/share").status_code, 401)
        self.assertEqual(Client().post("/api/share", "{}", content_type="application/json").status_code, 401)

    def test_preview_shows_what_would_go_out(self):
        body = signed_in().get("/api/share").json()
        self.assertEqual((body["setting"]["share"], body["setting"]["gemini"]), ("sealed", False), "既定は会社だけ・外部 AI なし")
        self.assertIsNone(body["preview"]["record"].get("store"), "会社だけの下書きに、平文の中身は出さない")
        ShareSetting.objects.filter(pk=1).update(share="post")
        body = signed_in().get("/api/share").json()
        self.assertEqual(body["setting"]["accept"], "following")
        self.assertTrue(body["preview"]["notable"])
        self.assertIn("ロボットC：停止", body["preview"]["post"])
        self.assertEqual(body["preview"]["record"]["stopped"], ["C"])
        self.assertEqual(body["collection"], "internal.robomori.report")

    def test_saving_a_choice_is_audited(self):
        client = signed_in()
        response = client.post("/api/share", json.dumps({"share": "record", "gemini": False, "accept": "listed",
                                                        "allow": ["@alice.bsky.social", "did:plc:abc"], "window_days": 3}),
                               content_type="application/json")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(ShareSetting.get().policy(), {"share": "record", "gemini": False, "accept": "listed",
                                                       "allow": ["alice.bsky.social", "did:plc:abc"], "window_days": 3, "recipients": []})
        self.assertEqual(response.json()["preview"]["post"], "")
        self.assertIn("記録だけ", Event.objects.get(source="share").text)
        client.post("/api/share", json.dumps({"share": "record"}), content_type="application/json")
        self.assertEqual(Event.objects.filter(source="share").count(), 1, "変わらなければ記録しない")

    def test_values_outside_the_choices_are_refused(self):
        client = signed_in()
        for bad in ({"share": "everything"}, {"accept": "?"}, {"gemini": "yes"}, {"window_days": 90}, {"window_days": "3"},
                    {"allow": ["<script>"]}, {"allow": {"did:plc:abc": 1}}, {"allow": [3]}, ["not", "an", "object"]):
            self.assertEqual(client.post("/api/share", json.dumps(bad), content_type="application/json").status_code, 400, bad)
        self.assertEqual(client.post("/api/share", "{", content_type="application/json").status_code, 400)
        self.assertEqual(ShareSetting.get().policy()["share"], "sealed")

    def test_recipients_are_keys_and_bad_keys_are_refused(self):
        from cryptography.hazmat.primitives.asymmetric import ec
        from seal import public_of
        key = public_of(ec.generate_private_key(ec.SECP256R1()))
        client = signed_in()
        ok = client.post("/api/share", json.dumps({"share": "sealed", "recipients": f"店長 {key}\n"}), content_type="application/json")
        self.assertEqual(ok.status_code, 200)
        self.assertEqual(ShareSetting.get().recipients, [{"name": "店長", "key": key}])
        bad = client.post("/api/share", json.dumps({"recipients": "店長 robomori-key:1:abc"}), content_type="application/json")
        self.assertEqual(bad.status_code, 400)

    def test_csrf_is_checked(self):
        client = Client(enforce_csrf_checks=True)
        client.cookies = signed_in().cookies   # 入るところは別に確かめている。ここでは入ったあとの POST だけ
        self.assertEqual(client.post("/api/share", json.dumps({"share": "off"}), content_type="application/json").status_code, 403)


class StandInTest(TestCase):
    def test_telemetry_has_the_unity_shape_and_shakes_on_the_bump(self):
        sim = StandIn()
        shaking = False
        for step in range(400):
            message = parse_telemetry(decode(sim.tick(step * 0.5, 0.5)))
            self.assertEqual([r["id"] for r in message["robots"]], ["A", "B", "C"])
            shaking |= any(r["vibration"] >= 0.4 for r in message["robots"])
        self.assertTrue(shaking)
        self.assertTrue(sim.boot.startswith("sim-"))

    def test_sends_the_floor_as_a_scene_like_unity(self):
        from protocol import parse_scene
        sim = StandIn()
        scene = parse_scene(decode(sim.scene_line()))
        self.assertEqual(scene["boot"], sim.boot)
        self.assertEqual(sum(t["kind"] == "table" for t in scene["things"]), 3)
        self.assertTrue(any(s["id"] == "home-B" for s in scene["spots"]))
        message = parse_telemetry(decode(sim.tick(0.0, 0.5)))
        self.assertTrue(all(r["says"].startswith("（代役）") for r in message["robots"]))

    def test_follows_advice_like_unity(self):
        sim = StandIn()
        zone = {"id": "通路中", "name": "通路中", "x": -1.0, "z": 0.0, "r": 1.8, "speed_scale": 0.6, "reason": "段差"}
        sim.on_advice(json.dumps({"v": 1, "type": "advice", "boot": "hub-1", "seq": 1, "robots": [], "zones": [zone], "proposals": []}).encode(), 0.0)
        self.assertAlmostEqual(sim.scale_of("A", -1.0, 0.5), 0.6)
        self.assertEqual(sim.scale_of("A", -6.0, 3.6), 1.0)
        sim.on_advice(json.dumps({"v": 1, "type": "advice", "boot": "hub-1", "seq": 2, "robots": [], "zones": [], "proposals": [
            {"id": "p-1", "zone": "通路中", "robot": "", "reason": "揺れがない", "speed_scale": 1.0}]}).encode(), 1.0)
        sim.on_advice(json.dumps({"v": 1, "type": "advice", "boot": "hub-1", "seq": 3, "robots": [], "zones": [], "proposals": [
            {"id": "p-1", "zone": "通路中", "robot": "", "reason": "揺れがない", "speed_scale": 1.0}]}).encode(), 30.0)
        self.assertEqual(sim.scale_of("A", -1.0, 0.5), 1.0)                  # 代役が承認して戻した
        sent = json.loads(sim.tick(31.0, 0.5))
        self.assertEqual((sent["approved"], sent["restored"]), (["p-1"], ["zone:通路中"]))

    def test_three_unusable_lines_close_door_one(self):
        sim = StandIn()
        sim.on_advice(b'{"v":1,"type":"advice","boot":"hub-1","seq":5,"robots":[{"id":"A","speed_scale":1.5}],"zones":[]}', 0.0)
        sim.on_advice(b'{"v":1,"type":"advice","boot":"hub-1","seq":2,"robots":[],"zones":[]}', 0.4)
        self.assertFalse(sim.closed(0.5))
        sim.on_advice(b'{"v":1,"type":"advice","seq":', 0.8)
        self.assertTrue(sim.closed(1.0))
        self.assertFalse(sim.closed(20.0))                                   # 代役は時間で開け直す

    def test_moves_on_the_unity_floor_one_robot_at_a_time_in_the_aisle(self):
        sim = StandIn()
        room = layout.ROOM
        for step in range(3000):
            message = json.loads(sim.tick(step * 0.1, 0.1))
            inside = [r["id"] for r in message["robots"] if abs(r["x"] - layout.CENTRAL_AISLE_X) < 0.6]
            self.assertLessEqual(len(inside), 1)                             # 単線通路: 中央の通路には 1 台ずつ
            for thing in message["robots"] + message["people"]:
                self.assertTrue(room["west"] < thing["x"] < room["east"] and room["south"] < thing["z"] < room["north"])
            self.assertFalse(any(r["stopped"] and abs(r["x"] - layout.CENTRAL_AISLE_X) < 0.6 for r in message["robots"]))


class Clock:
    def __init__(self, start, end, mode, pk=1):
        self.start, self.end, self.mode, self.pk = start, end, mode, pk


class ScheduleTest(TestCase):
    def test_slots_can_cross_midnight(self):
        night = Clock(time(21, 10), time(9, 0), "standalone")
        self.assertTrue(schedule.contains(night.start, night.end, time(23, 0)))
        self.assertTrue(schedule.contains(night.start, night.end, time(3, 0)))
        self.assertFalse(schedule.contains(night.start, night.end, time(12, 0)))
        early = datetime(2026, 10, 25, 3, 0)
        self.assertEqual(schedule.occurrence(early, night), "2026-10-24#1")   # 始まった日で数える

    def test_default_and_next_change(self):
        slots = [Clock(time(9, 0), time(21, 0), "store"), Clock(time(21, 0), time(21, 10), "export", 2)]
        self.assertEqual(schedule.current(datetime(2026, 10, 24, 8, 0), slots)[1], "share")
        moment, mode = schedule.next_change(datetime(2026, 10, 24, 20, 0), slots)
        self.assertEqual((moment.hour, moment.minute, mode), (21, 0, "export"))

    def test_parse_rejects_unknown_modes(self):
        self.assertEqual(schedule.parse("09:00-21:00 store、21:00-24:00 share")[1], (time(21, 0), time(0, 0), "share"))
        with self.assertRaises(ValueError):
            schedule.parse("09:00-21:00 open")

    def test_timetable_command(self):
        call_command("timetable", "--demo", stdout=open(os.devnull, "w"))
        self.assertEqual([s.mode for s in Slot.objects.all()], ["share", "export", "store", "standalone", "share"])
        self.assertEqual(schedule.current(timezone.localtime(), list(Slot.objects.all()))[1], "share")
        with self.assertRaises(CommandError):
            call_command("timetable", "--set", "9-21 store", stdout=open(os.devnull, "w"))
        self.assertEqual(signed_in().get("/api/state").json()["schedule"]["mode"], "share")


class OutboundTest(TestCase):
    def test_addresses_never_leave_the_store(self):
        from .outbound import build_snapshot
        Event.objects.create(source="door", kind="door1", text="扉 1: Unity（または代役）がつながった（172.19.0.3:46712）")
        Event.objects.create(source="ai", kind="ai", text="場所の問題: 通路中 の速度を 0.6 倍に下げた")
        texts = [r["text"] for r in build_snapshot(Gate.get())["records"]]
        self.assertIn("扉 1: Unity（または代役）がつながった", texts)
        self.assertFalse(any("172.19" in t for t in texts))
        self.assertIn("場所の問題: 通路中 の速度を 0.6 倍に下げた", texts)       # 倍率などの数字は残す


class ScheduleLoopTest(TestCase):
    def setUp(self):
        self.calls = []
        self.loop = CoreLoop(CoreHub(Monitor(learn_seconds=0), log=say_to_log), lambda data: None, lambda: False,
                             set_accepting=self.calls.append)

    def test_standalone_slot_refuses_unity(self):
        Slot.objects.create(start=time(0, 0), end=time(0, 0), mode="standalone")
        self.loop.tick(0.0)
        self.assertEqual(self.calls, [False])
        self.assertEqual(Live.get().door1, "standalone")
        Slot.objects.all().delete()
        self.loop.tick(2.0)
        self.assertEqual(self.calls, [False, True])

    def test_export_slot_writes_drive_once_without_going_online(self):
        now = timezone.localtime()
        Slot.objects.create(start=(now - timedelta(minutes=1)).time(), end=(now + timedelta(minutes=5)).time(), mode="export")
        Event.objects.create(source="ai", kind="ai", text="場所の問題: 通路中 の速度を 0.6 倍に下げた")
        with tempfile.TemporaryDirectory() as folder, mock.patch.dict(os.environ, {"EXPORT_DIR": folder}):
            self.loop.tick(0.0)
            self.assertEqual(Gate.get().state, "external")
            self.assertEqual(len(list(Path(folder).glob("*.md"))), 1)
            self.loop.tick(1.5)
            self.loop.tick(4.0)                                   # 少し見せてから閉じる
            self.assertEqual(Gate.get().state, "store")
            self.loop.tick(6.0)
            self.assertEqual(len(list(Path(folder).glob("*.md"))), 1)   # 同じ区間では 1 回だけ
            report = next(Path(folder).glob("*.md")).read_text(encoding="utf-8")
            self.assertIn("時間割の書き出し", report)


class RuleBotTest(TestCase):
    """ルール AI: rules.json の数字だけで判断し、根拠の数字つきで答える。外には出ない。"""

    def setUp(self):
        from . import rulebot
        self.rulebot = rulebot
        self.rules, _ = rulebot.load_rules()
        self.now = timezone.now()

    def ev(self, minutes_ago, source, kind, **data):
        return {"at": self.now - timedelta(minutes=minutes_ago), "source": source, "kind": kind, "data": data}

    def test_repeated_stops_and_m5_recoveries_make_a_robot_dangerous(self):
        events = [self.ev(m, "robot", "stop", robot="B") for m in (2, 6, 12, 20)]          # 30 分で 4 回 → 危険
        events += [self.ev(40, "robot", "stop", robot="A")]                                 # 30 分より前は数えない
        events += [self.ev(3, "relay", "key2", accepted=True), self.ev(1, "relay", "key2", accepted=True)]
        robots = [{"id": "A", "condition": "Normal", "stopped": False}, {"id": "B", "condition": "Warning", "stopped": True},
                  {"id": "C", "condition": "Normal", "stopped": False}]
        minute = int(self.now.timestamp() // 60)
        result = self.rulebot.judge(self.rules, robots, events, {"robot": "B", "peaks": [[minute, 1898]]}, self.now)
        by = {r["id"]: r for r in result["robots"]}
        self.assertEqual((by["A"]["level"], by["C"]["level"]), ("safe", "safe"))
        self.assertEqual(by["B"]["level"], "danger")
        self.assertEqual(by["B"]["values"]["stops"], 4)
        self.assertEqual(by["B"]["values"]["m5_peak"], 1898)
        self.assertEqual(by["A"]["values"]["m5_key2"], None, "M5 につながっていないロボットは M5 の値を持たない")
        self.assertEqual(result["store"]["values"]["robots_stopped"], 1)

        status = self.rulebot.answer("いま危ない？", self.rules, result)
        self.assertEqual(status["level"], "danger")
        self.assertIn("ロボット B: 危険", status["answer"])
        detail = self.rulebot.answer("ロボットＢは大丈夫？", self.rules, result)   # 全角でも分かる
        self.assertEqual(detail["robot"], "B")
        self.assertIn("止まった回数: 30 分で 4回（危険の基準 4回 以上）", detail["answer"])
        advice = self.rulebot.answer("どうすればいい？", self.rules, result)
        self.assertIn("点検", advice["answer"])
        self.assertEqual(self.rulebot.answer("止まった回数は？", self.rules, result)["intent"], "stops")
        self.assertIn("1 台で注意が 3 つ以上", self.rulebot.answer("判断の基準は？", self.rules, result)["answer"])

    def test_three_cautions_together_make_danger(self):
        events = [self.ev(m, "robot", "stop", robot="A") for m in (1, 5)]                          # 止まった 2 回: 注意
        events += [self.ev(m, "robot", "condition", robot="A", to="Warning") for m in (2, 8)]       # 注意・制限 2 回: 注意
        robots = [{"id": "A", "condition": "Limited", "stopped": False}]                             # いま制限: 注意
        robot = self.rulebot.judge(self.rules, robots, events, {}, self.now)["robots"][0]
        self.assertEqual((robot["level"], robot["combined"]), ("danger", True))

    def test_bad_rules_are_refused_with_the_place(self):
        with self.assertRaisesRegex(self.rulebot.RulesError, r"robot_rules\[0\]\.metric"):
            self.rulebot.validate({"robot_rules": [{"id": "x", "label": "x", "metric": "nope", "caution": 1}], "store_rules": []})
        with self.assertRaisesRegex(self.rulebot.RulesError, "danger を caution 以上"):
            self.rulebot.validate({"robot_rules": [{"id": "x", "label": "x", "metric": "stops", "caution": 3, "danger": 1}],
                                   "store_rules": [{"id": "y", "label": "y", "metric": "store_stops", "caution": 1}]})

    def test_core_records_stops_and_the_screen_can_ask(self):
        hub = CoreHub(Monitor())
        loop = CoreLoop(hub, lambda data: None, lambda: True)
        loop._note_robot_changes([{"id": "B", "stopped": False, "condition": "Normal"}], True)   # 基準にするだけ
        loop._note_robot_changes([{"id": "B", "stopped": True, "condition": "Warning"}], True)
        loop.recorder.flush()
        self.assertEqual(sorted(Event.objects.filter(source="robot").values_list("kind", flat=True)), ["condition", "stop"])
        Live.objects.update_or_create(pk=1, defaults={"unity_connected": True,
                                                      "robots": [{"id": "B", "condition": "Warning", "stopped": True}]})
        client = signed_in()
        self.assertEqual(client.get("/api/judge").json()["robots"][0]["values"]["stops"], 1)
        reply = client.post("/api/ask", json.dumps({"q": "B は？"}), content_type="application/json").json()
        self.assertIn("止まった 1 回", reply["answer"])
        self.assertTrue(Event.objects.filter(source="rulebot", kind="ask").exists())
        self.assertEqual(client.post("/api/ask", json.dumps({"q": ""}), content_type="application/json").status_code, 400)
        self.assertEqual(Client().post("/api/ask", json.dumps({"q": "B"}), content_type="application/json").status_code, 401)
