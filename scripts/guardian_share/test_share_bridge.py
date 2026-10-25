"""共有中継のテスト。実行: python -m unittest discover -s scripts/guardian_share

Bluesky には接続しない（通信は偽物に差し替える）。PSoC 6 の main.py も、machine と MicroPython の time を
まねた部品で CPython 上で動かして確かめる。
"""
import io
import json
import runpy
import sys
import tempfile
import types
import unittest
from datetime import datetime, timedelta
from pathlib import Path

from bluesky import BlueskyClient, BlueskyError, flatten_replies, tag_facets, web_url
from report import compose_post, md_escape, render_html, render_markdown
from share_bridge import Archive, LogFollower, ShareDesk

DID = "did:plc:abc123xyz"
POST_URI = f"at://{DID}/app.bsky.feed.post/3kpost1"
FIRMWARE = Path(__file__).resolve().parents[2] / "firmware" / "psoc6_share_panel" / "main.py"


def record(kind, **fields):
    return {"wall": "2026-10-24T10:15:02.000", "mono": 1.0, "kind": kind, **fields}


class FakePoster:
    def __init__(self, fail=None):
        self.posts = []
        self.thread = []
        self.fail = fail

    def post(self, text, facets):
        if self.fail:
            raise self.fail
        self.posts.append((text, facets))
        return {"uri": POST_URI, "cid": "bafy1", "url": web_url(POST_URI, "robomori.bsky.social"),
                "handle": "robomori.bsky.social", "created_at": "2026-10-24T01:15:10.000Z"}

    def replies(self, uri):
        return list(self.thread)


def reply(rkey, handle, text, created):
    return {"uri": f"at://did:plc:other/app.bsky.feed.post/{rkey}", "url": f"https://bsky.app/profile/{handle}/post/{rkey}",
            "handle": handle, "name": "", "text": text, "created_at": created, "depth": 1}


class BlueskyTest(unittest.TestCase):
    def test_tag_facets_use_utf8_byte_offsets(self):
        text = "振動が増えた\n#ロボもり #JPHACKS"
        facets = tag_facets(text, ["ロボもり", "JPHACKS"])
        data = text.encode("utf-8")
        self.assertEqual([data[f["index"]["byteStart"]:f["index"]["byteEnd"]].decode() for f in facets], ["#ロボもり", "#JPHACKS"])
        self.assertEqual(facets[0]["features"][0], {"$type": "app.bsky.richtext.facet#tag", "tag": "ロボもり"})

    def test_tag_facet_skips_a_longer_word(self):
        self.assertEqual(tag_facets("#JPHACKS2026", ["JPHACKS"]), [])

    def test_web_url_only_for_well_formed_post_uris(self):
        self.assertEqual(web_url(POST_URI, "robomori.bsky.social"), "https://bsky.app/profile/robomori.bsky.social/post/3kpost1")
        self.assertEqual(web_url(POST_URI), f"https://bsky.app/profile/{DID}/post/3kpost1")
        self.assertIsNone(web_url("at://did:plc:abc/app.bsky.feed.like/1"))
        self.assertIsNone(web_url('at://did:plc:abc/app.bsky.feed.post/"><script>'))

    def test_flatten_replies_walks_nested_threads_and_skips_hidden_posts(self):
        def view(rkey, handle, text, created, replies=()):
            return {"$type": "app.bsky.feed.defs#threadViewPost",
                    "post": {"uri": f"at://did:plc:x/app.bsky.feed.post/{rkey}", "author": {"handle": handle, "displayName": handle.upper()},
                             "record": {"text": text, "createdAt": created}},
                    "replies": list(replies)}
        thread = view("root", "me.test", "root", "2026-10-24T01:00:00Z", [
            view("b", "bob.test", "軸受を見て", "2026-10-24T01:02:00Z", [view("c", "carla.test", "同意", "2026-10-24T01:03:00Z")]),
            {"$type": "app.bsky.feed.defs#notFoundPost", "uri": "at://x", "notFound": True},
            view("a", "alice.test", "固定部の緩み", "2026-10-24T01:01:00Z"),
        ])
        replies = flatten_replies(thread)
        self.assertEqual([r["text"] for r in replies], ["固定部の緩み", "軸受を見て", "同意"])
        self.assertEqual([r["depth"] for r in replies], [1, 1, 2])
        self.assertEqual(replies[0]["url"], "https://bsky.app/profile/alice.test/post/a")

    def test_replies_carry_the_author_did_and_cid(self):
        thread = {"post": {}, "replies": [{"post": {"uri": "at://did:plc:alice/app.bsky.feed.post/a", "cid": "bafya",
                                                    "author": {"did": "did:plc:alice", "handle": "alice.test"},
                                                    "record": {"text": "見て", "createdAt": "2026-10-24T01:01:00Z"},
                                                    "indexedAt": "2026-10-24T01:01:02Z"}},
                                          {"post": {"uri": "at://x/app.bsky.feed.post/b", "author": {"did": "<b>", "handle": "b.test"},
                                                    "record": {"text": "x"}}}]}
        replies = {r["handle"]: r for r in flatten_replies(thread)}
        first, second = replies["alice.test"], replies["b.test"]
        self.assertEqual((first["did"], first["cid"], first["indexed_at"]), ("did:plc:alice", "bafya", "2026-10-24T01:01:02Z"))
        self.assertEqual(second["did"], "", "DID の形でないものは使わない")

    def test_follows_pages_through_and_handles_resolve_to_dids(self):
        seen = []

        def transport(method, url, body=None, token=None):
            seen.append((method, url, token))
            if "getFollows" in url:
                if "cursor=" not in url:
                    return {"follows": [{"did": "did:plc:a"}, {"did": "bad"}], "cursor": "next"}
                return {"follows": [{"did": "did:plc:b"}]}
            return {"did": "did:plc:alice"}

        client = BlueskyClient("x", "y", transport=transport)
        self.assertEqual(client.follows("did:plc:store"), {"did:plc:a", "did:plc:b"})
        self.assertEqual(client.resolve_handle("alice.test"), "did:plc:alice")
        self.assertTrue(all(m == "GET" and token is None and url.startswith("https://public.api.bsky.app/xrpc/") for m, url, token in seen),
                        "公開 AppView を、ログインせずに読む")
        with self.assertRaises(ValueError):
            client.resolve_handle("not a handle/../")

    def test_post_logs_in_once_and_creates_a_post_record(self):
        calls = []

        def transport(method, url, body=None, token=None):
            calls.append((method, url, body, token))
            if url.endswith("createSession"):
                return {"accessJwt": "jwt-1", "did": DID, "handle": "robomori.bsky.social"}
            return {"uri": POST_URI, "cid": "bafy1"}

        client = BlueskyClient("robomori.bsky.social", "app-pass", transport=transport)
        result = client.post("テスト #ロボもり", tag_facets("テスト #ロボもり", ["ロボもり"]))
        client.post("二件目")
        self.assertEqual([c[1].rsplit("/", 1)[1] for c in calls], ["com.atproto.server.createSession",
                                                                  "com.atproto.repo.createRecord", "com.atproto.repo.createRecord"])
        body, token = calls[1][2], calls[1][3]
        self.assertEqual((body["repo"], body["collection"], token), (DID, "app.bsky.feed.post", "jwt-1"))
        self.assertEqual(body["record"]["text"], "テスト #ロボもり")
        self.assertEqual(body["record"]["langs"], ["ja"])
        self.assertTrue(body["record"]["createdAt"].endswith("Z"))
        self.assertEqual(len(body["record"]["facets"]), 1)
        self.assertEqual(result["url"], "https://bsky.app/profile/robomori.bsky.social/post/3kpost1")

    def test_expired_token_logs_in_again_once(self):
        state = {"posts": 0, "logins": 0}

        def transport(method, url, body=None, token=None):
            if url.endswith("createSession"):
                state["logins"] += 1
                return {"accessJwt": f"jwt-{state['logins']}", "did": DID}
            state["posts"] += 1
            if token == "jwt-1":
                raise BlueskyError(400, "ExpiredToken", "Token has expired")
            return {"uri": POST_URI, "cid": "bafy1"}

        client = BlueskyClient("robomori.bsky.social", "app-pass", transport=transport)
        client.post("テスト")
        self.assertEqual(state, {"posts": 2, "logins": 2})

    def test_wrong_password_is_not_retried(self):
        def transport(method, url, body=None, token=None):
            raise BlueskyError(401, "AuthenticationRequired", "Invalid identifier or password")

        with self.assertRaises(BlueskyError):
            BlueskyClient("robomori.bsky.social", "wrong", transport=transport).post("テスト")

    def test_too_long_post_is_refused_before_sending(self):
        client = BlueskyClient("x", "y", transport=lambda *a, **k: self.fail("must not send"))
        with self.assertRaises(ValueError):
            client.post("あ" * 301)


def sample_incident(**overrides):
    incident = {"number": 7, "id": "incident-007", "robot": "B", "level": "Limited", "detected_at": "2026-10-24T10:15:02.000",
                "measured": True, "recovered_at": None, "shared": None, "replies": [],
                "history": [{"at": "2026-10-24T10:15:00.000", "level": "Warning", "reason": "注意"},
                            {"at": "2026-10-24T10:15:02.000", "level": "Limited", "reason": "制限"}],
                "reason": "振動 300 mg 以上が 6 回続いたため制限（今回 412 mg）",
                "samples": [{"at": "2026-10-24T10:15:01.000", "rms_mg": 405.3, "peak_mg": 900.0},
                            {"at": "2026-10-24T10:15:02.000", "rms_mg": 412.0, "peak_mg": 950.0}],
                "threshold_mg": 300.0, "warning_count": 2, "limited_count": 6}
    incident.update(overrides)
    return incident


class ReportTest(unittest.TestCase):
    def test_post_states_robot_level_reason_and_source_within_limit(self):
        text = compose_post(sample_incident(), ["ロボもり", "JPHACKS"])
        self.assertLessEqual(len(text), 300)
        self.assertIn("#007", text)
        self.assertIn("配膳ロボット B：制限（注意 → 制限）", text)
        self.assertIn("412 mg（基準 300 mg）", text)
        self.assertIn("M5StickS3 で実測", text)
        self.assertTrue(text.endswith("#ロボもり #JPHACKS"))

    def test_demo_post_says_it_is_simulated(self):
        self.assertIn("模擬データ", compose_post(sample_incident(measured=False), []))

    def test_long_reason_is_shortened_to_fit(self):
        text = compose_post(sample_incident(reason="長い理由" * 100), ["ロボもり"])
        self.assertLessEqual(len(text), 300)
        self.assertIn("…", text)

    def test_reply_text_is_escaped_in_html_and_left_raw_in_markdown(self):
        hostile = reply("r1", "mallory.test", '<script>alert(1)</script> ![x](http://tracker.example/p.png)', "2026-10-24T01:20:00Z")
        hostile["name"] = "[click](http://evil.example)"
        incident = sample_incident(replies=[hostile])
        page = render_html(incident)
        self.assertNotIn("<script>", page)
        self.assertIn("&lt;script&gt;", page)
        markdown = render_markdown(incident)
        self.assertIn("    <script>alert(1)</script> ![x](http://tracker.example/p.png)", markdown)  # 字下げのコードブロック
        self.assertIn(md_escape("[click](http://evil.example)"), markdown)
        self.assertNotIn("（[click](", markdown)

    def test_report_separates_facts_decision_and_replies(self):
        markdown = render_markdown(sample_incident(replies=[reply("r1", "alice.test", "固定部の緩みかも", "2026-10-24T01:20:00Z")]))
        for heading in ("## 実測データ", "## Guardian の判断", "## Bluesky での共有", "## 返信（外部からの意見。検証していない仮説）"):
            self.assertIn(heading, markdown)
        self.assertIn("412.0 mg（基準の 1.4 倍）", markdown)
        self.assertIn("    固定部の緩みかも", markdown)


class DeskTest(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.messages = []

    def tearDown(self):
        self.directory.cleanup()

    def desk(self, poster=None):
        return ShareDesk(Archive(self.root), poster, ["ロボもり"], log=self.messages.append)

    def feed_relay(self, desk, replay=False):
        desk.on_log_record(record("start", robot="B", threshold_mg=300.0, warning_count=2, limited_count=6), replay)
        for rms in (12.0, 340.0, 405.0):
            desk.on_log_record(record("sample", rms_mg=rms, peak_mg=rms * 2, warmup=False), replay)
        desk.on_log_record(record("level", level="Warning", hold=False, reason="振動 300 mg 以上が 2 回続いたため注意"), replay)

    def test_old_records_set_context_but_open_no_incident(self):
        desk = self.desk()
        self.feed_relay(desk, replay=True)
        self.assertIsNone(desk.pending)
        self.assertEqual(len(desk.samples), 3)

    def test_escalation_before_sharing_stays_one_incident(self):
        desk = self.desk()
        self.feed_relay(desk)
        desk.on_log_record(record("level", level="Limited", hold=False, reason="6 回続いたため制限"))
        self.assertEqual(desk.pending["id"], "incident-001")
        self.assertEqual([h["level"] for h in desk.pending["history"]], ["Warning", "Limited"])
        self.assertEqual(desk.pending["level"], "Limited")
        self.assertEqual([s["rms_mg"] for s in desk.pending["samples"]], [12.0, 340.0, 405.0])
        for name in ("incidents/incident-001.json", "reports/incident-001.md", "reports/incident-001.html"):
            self.assertTrue((self.root / name).exists(), name)

    def test_warmup_samples_are_left_out(self):
        desk = self.desk()
        desk.on_log_record(record("sample", rms_mg=90.0, warmup=True))
        self.assertEqual(len(desk.samples), 0)

    def test_dry_run_approval_records_without_posting(self):
        desk = self.desk(poster=None)
        self.feed_relay(desk)
        self.assertEqual(desk.approve("SW2", 10.0), "shared")
        incident = json.loads((self.root / "incidents/incident-001.json").read_text(encoding="utf-8"))
        self.assertTrue(incident["shared"]["dry_run"])
        self.assertIn("配膳ロボット B", incident["shared"]["text"])
        self.assertIsNone(desk.pending)
        self.assertEqual(desk.poll_replies(), 0)

    def test_approval_posts_once_and_next_press_does_nothing(self):
        poster = FakePoster()
        desk = self.desk(poster)
        self.feed_relay(desk)
        self.assertEqual(desk.approve("SW2", 10.0), "shared")
        self.assertEqual(desk.approve("SW2", 10.5), "idle")
        self.assertEqual(len(poster.posts), 1)
        text, facets = poster.posts[0]
        self.assertIn("#ロボもり", text)
        self.assertEqual(facets[0]["features"][0]["tag"], "ロボもり")
        self.assertEqual(desk.last["shared"]["url"], "https://bsky.app/profile/robomori.bsky.social/post/3kpost1")

    def test_failed_post_keeps_the_incident_waiting(self):
        desk = self.desk(FakePoster(fail=OSError("network unreachable")))
        self.feed_relay(desk)
        self.assertEqual(desk.approve("SW2", 10.0), "error")
        self.assertIsNotNone(desk.pending)
        self.assertIsNone(desk.pending["shared"])
        self.assertEqual({desk.lamp(10.0 + i * 0.2) for i in range(5)}, {True, False})  # 失敗は速い点滅

    def test_replies_are_added_once_and_own_replies_left_out(self):
        poster = FakePoster()
        desk = self.desk(poster)
        self.feed_relay(desk)
        desk.approve("SW2", 10.0)
        poster.thread = [reply("r1", "alice.test", "固定部の緩み", "2026-10-24T01:20:00Z"),
                         reply("r0", "robomori.bsky.social", "補足です", "2026-10-24T01:19:00Z")]
        self.assertEqual(desk.poll_replies(), 1)
        self.assertEqual(desk.poll_replies(), 0)
        poster.thread.append(reply("r2", "bob.test", "軸受", "2026-10-24T01:25:00Z"))
        self.assertEqual(desk.poll_replies(), 1)
        saved = json.loads((self.root / "incidents/incident-001.json").read_text(encoding="utf-8"))
        self.assertEqual([r["handle"] for r in saved["replies"]], ["alice.test", "bob.test"])
        self.assertIn("軸受", (self.root / "reports/incident-001.md").read_text(encoding="utf-8"))
        self.assertTrue(desk.lamp(100.0))           # 未確認の返信があるあいだは点灯
        self.assertEqual(desk.approve("SW2", 100.0), "seen")
        self.assertFalse(desk.lamp(200.0))

    def test_pending_lamp_blinks_and_idle_lamp_is_off(self):
        desk = self.desk()
        self.assertFalse(desk.lamp(1.0))
        self.feed_relay(desk)
        self.assertEqual({desk.lamp(t / 10) for t in range(0, 20, 2)}, {True, False})

    def test_key2_recovery_is_noted_on_the_waiting_incident(self):
        desk = self.desk()
        self.feed_relay(desk)
        desk.on_log_record(record("key2", accepted=True, reason="KEY2 で人が復帰"))
        self.assertEqual(desk.pending["recovered_at"], "2026-10-24T10:15:02.000")
        self.assertIn("人が正常に戻しました", compose_post(desk.pending, []))

    def test_hold_opens_an_incident(self):
        desk = self.desk()
        self.feed_relay(desk, replay=True)
        desk.on_log_record(record("hold", reason="センサーのデータが 3.1 秒途切れたため保持"))
        self.assertEqual(desk.pending["level"], "Hold")
        self.assertIn("保持（停止）", compose_post(desk.pending, []))

    def test_numbers_continue_and_posted_incidents_are_followed_after_restart(self):
        poster = FakePoster()
        desk = self.desk(poster)
        self.feed_relay(desk)
        desk.approve("SW2", 10.0)
        restarted = self.desk(poster)
        self.assertEqual([i["id"] for i in restarted.followed], ["incident-001"])
        self.feed_relay(restarted)
        self.assertEqual(restarted.pending["id"], "incident-002")

    def test_demo_incident_is_marked_simulated(self):
        desk = self.desk()
        incident = desk.open_demo()
        self.assertFalse(incident["measured"])
        self.assertEqual(incident["level"], "Limited")
        self.assertIn("模擬", compose_post(incident, []))


class LogFollowerTest(unittest.TestCase):
    def test_file_from_before_start_is_replayed_and_new_file_is_live(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            old = directory / "20261024-100000.jsonl"
            old.write_text(json.dumps(record("level", level="Warning")) + "\n", encoding="utf-8")
            follower = LogFollower(directory)
            self.assertEqual([replay for _, replay in follower.poll(0.0)], [True])
            with old.open("a", encoding="utf-8") as log:
                log.write(json.dumps(record("level", level="Limited")) + "\n" + '{"kind": "sam')
            self.assertEqual([(r["level"], replay) for r, replay in follower.poll(0.1)], [("Limited", False)])
            with old.open("a", encoding="utf-8") as log:
                log.write('ple", "rms_mg": 1.0}\n')
            self.assertEqual([r["kind"] for r, _ in follower.poll(0.2)], ["sample"])  # 途中で切れた行はつながってから読む
            (directory / "20261024-110000.jsonl").write_text(json.dumps(record("start")) + "\n", encoding="utf-8")
            self.assertEqual([(r["kind"], replay) for r, replay in follower.poll(5.0)], [("start", False)])
            follower.close()


class Psoc6CheckTest(unittest.TestCase):
    def test_line_in_the_main_shape_passes(self):
        from psoc6_check import check_line
        self.assertEqual(check_line({"type": "event", "dev": "psoc6-01", "seq": 3, "t_ms": 10, "event": "button", "btn": "SW2"}, 2), [])
        self.assertEqual(check_line({"type": "sample", "dev": "sticks3-01", "seq": 0, "t_ms": 0}, None), [])

    def test_line_out_of_shape_is_explained(self):
        from psoc6_check import check_line
        problems = check_line({"type": "hello", "seq": 5, "t_ms": -1}, 3)
        self.assertEqual(len(problems), 4)   # type、dev、seq の飛び、t_ms
        self.assertTrue(any("seq が飛んだ" in p for p in problems))
        self.assertEqual(len(check_line({"type": "event", "dev": "x", "seq": True, "t_ms": 1, "event": "button"}, None)), 2)


class FirmwareTest(unittest.TestCase):
    """PSoC 6 の main.py を、MicroPython の部品をまねて CPython で動かす。"""

    def run_firmware(self, script):
        """script: PC から届く行と、"press"（その行の前に SW2 を押す）を並べたもの。"""
        out = io.StringIO()
        clock = {"ms": 1000}
        pins = {}

        class FakePin:
            OUT, IN, PULL_UP, IRQ_FALLING = "out", "in", "up", "falling"

            def __init__(self, name, mode=None, pull=None, value=None):
                self.name, self.level, self.handler = name, value, None
                pins[name] = self

            def irq(self, handler=None, trigger=None):
                self.handler = handler

            def value(self, level=None):
                self.level = level

        class Done(Exception):
            pass

        class FakeStdin:
            def __init__(self):
                self.items = list(script)

            def readline(self):
                while self.items and self.items[0] == "press":
                    self.items.pop(0)
                    pins["P0_4"].handler(pins["P0_4"])
                    pins["P0_4"].handler(pins["P0_4"])  # チャタリングで 2 回入る
                if not self.items:
                    raise Done
                clock["ms"] += 200
                return self.items.pop(0)

        machine = types.ModuleType("machine")
        machine.Pin = FakePin
        mtime = types.ModuleType("time")
        mtime.ticks_ms = lambda: clock["ms"]
        mtime.ticks_add = lambda a, b: a + b
        mtime.ticks_diff = lambda a, b: a - b
        saved = {name: sys.modules.get(name) for name in ("machine", "time")}
        saved_io = sys.stdin, sys.stdout
        sys.modules["machine"], sys.modules["time"] = machine, mtime
        sys.stdin, sys.stdout = FakeStdin(), out
        try:
            runpy.run_path(str(FIRMWARE))
        except Done:
            pass
        finally:
            sys.stdin, sys.stdout = saved_io
            for name, module in saved.items():
                if module is None:
                    sys.modules.pop(name, None)
                else:
                    sys.modules[name] = module
        return [json.loads(line) for line in out.getvalue().splitlines()], pins

    def test_hello_lamp_and_one_event_per_press(self):
        lamp_on = '{"cmd": "lamp", "on": true}\r\n'
        lamp_off = '{"cmd": "lamp", "on": false}\n'
        lines, pins = self.run_firmware([lamp_on, "press", lamp_off, lamp_off, "press", lamp_on])
        # StickS3 と同じ形: type と dev と seq と t_ms、中身は event
        self.assertTrue(all(line["type"] == "event" and line["dev"] == "psoc6-01" for line in lines))
        self.assertEqual([line["seq"] for line in lines], list(range(1, len(lines) + 1)))
        self.assertEqual(lines[0]["event"], "hello")
        buttons = [line for line in lines if line["event"] == "button"]
        self.assertEqual(len(buttons), 2)  # チャタリングは 1 回に数える
        self.assertEqual(buttons[0]["btn"], "SW2")
        self.assertEqual(pins["P13_7"].level, 0)  # 最後は点灯（0 で点灯）

    def test_hello_on_request_and_old_lamp_form(self):
        lines, pins = self.run_firmware(['{"cmd": "hello"}\n', '{"type": "lamp", "on": true}\n'])
        self.assertEqual([line["event"] for line in lines], ["hello", "hello"])
        self.assertEqual(pins["P13_7"].level, 0)

    def test_garbage_lines_are_ignored(self):
        lines, pins = self.run_firmware(["hello\n", "{not json\n", "[1, 2]\n", '{"cmd": "lamp", "on": "yes"}\n'])
        self.assertEqual([line["event"] for line in lines], ["hello"])
        self.assertEqual(pins["P13_7"].level, 1)   # on が true でなければ消す


if __name__ == "__main__":
    unittest.main()
