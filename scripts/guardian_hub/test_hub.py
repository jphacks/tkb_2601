"""監視ハブのテスト。実行: python -m unittest discover -s scripts/guardian_hub

Unity の JsonUtility が実際に出しうる行（長い桁の float、NaN / Infinity、BOM、CRLF、知らない項目、項目の欠け）を
そのまま流しても、ハブが止まらず、使える値だけを使うことを確かめる。
"""
import base64
import json
import math
import tempfile
import unittest
from pathlib import Path

from dispatch import Failure, NODES, nearest_node, recommend, shortest_path
from hub import Hub
from monitor import AREA_SCALE, ROBOT_SCALE, Monitor
from protocol import Rejected, decode, encode, parse_scene, parse_telemetry
from door2 import GeminiClient, GeminiError, Outside, compose_post, normalize_policy, parse_judgement, render_report


def robot(rid, node="待機場所", vibration=0.12, **overrides):
    x, z = NODES[node]
    item = {"id": rid, "x": x, "z": z, "speed": 0.0, "condition": "Normal", "stopped": False, "held": False,
            "task": "", "phase": "", "carrying": False, "vibration": vibration, "speed_scale": 1.0, "wait": ""}
    item.update(overrides)
    return item


def telemetry(seq, robots, people=(), boot="unity-test", **extra):
    return {"v": 1, "type": "telemetry", "boot": boot, "seq": seq, "t": seq * 0.5, "door": "store",
            "robots": list(robots), "people": list(people), "approved": [], "declined": [], **extra}


def line(message):
    return json.dumps(message).encode()


# Unity の JsonUtility が書く形（float の長い桁、NaN、生成直後の位置なし）
UNITY_LINE = ('﻿{"v":1,"type":"telemetry","boot":"unity-20261024-101500","seq":3,"t":12.340000152587891,"door":"store",'
              '"robots":[{"id":"A","x":-4.199999809265137,"z":-5.25,"speed":0.0,"condition":"Normal","stopped":false,'
              '"held":false,"task":"","phase":"","carrying":false,"vibration":0.11999999731779099,"speed_scale":1.0,"wait":""},'
              '{"id":"B","x":NaN,"z":0.0,"speed":Infinity,"condition":"Warning","stopped":false,"held":false,"task":"Food",'
              '"phase":"ToDropoff","carrying":true,"vibration":0.5,"speed_scale":1.0,"wait":"waiting for a clear route"},'
              '{"id":"C","x":-4.199999809265137,"z":4.9,"speed":1.25,"condition":"Unknown","stopped":false,"held":false,'
              '"task":"Soup","phase":"ToPickup","carrying":false,"vibration":-0.2,"speed_scale":7.0,"wait":"","extra":{"new":1}}],'
              '"people":[{"id":"staff","role":"floor","x":1.600000023841858,"z":-4.289999961853027,"busy":false}],'
              '"approved":[],"declined":[],"future_field":[1,2,3]}\r\n')


# Unity の GuardianHubLink がシーンから読んで送る配置（JsonUtility の形）
UNITY_SCENE = ('{"v":1,"type":"scene","boot":"unity-20261025-101500","seq":1,"west":-9.739999771118164,"east":9.74,'
               '"south":-6.04,"north":6.04,"things":[{"id":"Dining Table 1","kind":"table","label":"テーブル1","x":6.099999904632568,'
               '"z":-3.4,"w":1.9,"d":1.3},{"id":"Chair 1-0-0","kind":"chair","label":"","x":4.8,"z":-3.86,"w":0.5,"d":0.5},'
               '{"id":"Bad","kind":"table","label":"x","x":500,"z":0,"w":1,"d":1},{"id":"Flat","kind":"mark","label":"1","x":0,"z":0,"w":0,"d":0.5}],'
               '"spots":[{"id":"home-A","kind":"home","label":"A 待機","x":-4.2,"z":-5.25},{"id":"far","kind":"home","label":"","x":1e9,"z":0}]}')


class SceneTest(unittest.TestCase):
    def test_scene_keeps_readable_things_and_drops_out_of_range_ones(self):
        scene = parse_scene(decode(UNITY_SCENE))
        self.assertEqual(scene["room"]["west"], -9.739999771118164)
        self.assertEqual([t["id"] for t in scene["things"]], ["Dining Table 1", "Chair 1-0-0"])
        self.assertEqual(scene["things"][0]["x"], 6.1)
        self.assertEqual([s["id"] for s in scene["spots"]], ["home-A"])

    def test_scene_without_a_room_or_things_is_rejected(self):
        with self.assertRaises(Rejected):
            parse_scene({"v": 1, "type": "scene", "west": 1, "east": -1, "south": 0, "north": 1, "things": []})
        with self.assertRaises(Rejected):
            parse_scene({"v": 1, "type": "scene", "west": -1, "east": 1, "south": -1, "north": 1, "things": []})

    def test_hub_keeps_the_scene_and_counts_only_real_changes(self):
        said = []
        hub = Hub(Monitor(learn_seconds=0), log=said.append)
        self.assertTrue(hub.on_line(UNITY_SCENE.encode("utf-8"), 0.0))
        self.assertEqual(hub.scene_rev, 1)
        self.assertTrue(hub.on_line(UNITY_SCENE.replace('"seq":1', '"seq":2').encode("utf-8"), 1.0))
        self.assertEqual(hub.scene_rev, 1)   # 同じ配置の送り直しでは描き直さない
        self.assertTrue(hub.on_line(UNITY_SCENE.replace('"z":-3.4', '"z":-3.0').encode("utf-8"), 2.0))
        self.assertEqual(hub.scene_rev, 2)
        self.assertEqual(hub.rejected, 0)
        self.assertTrue(any("店内の配置" in s for s in said))

    def test_telemetry_carries_guests_seats_and_what_a_robot_said(self):
        message = telemetry(1, [robot("A", says="ロボット A です。テーブル1 へスープを運んでいます", table=1)],
                            people=[{"id": "Diner-1", "role": "guest", "state": "食事中", "x": 4.8, "z": -3.4}],
                            seats=[{"id": "Chair 1-1-0", "state": "occupied", "table": 1}, {"id": "x", "state": "nope", "table": 1}],
                            talk={"robot": "A", "question": "今の状況は？", "answer": "スープを運んでいます", "by": "あなた", "n": 1})
        parsed = parse_telemetry(message)
        self.assertEqual(parsed["robots"][0]["says"], "ロボット A です。テーブル1 へスープを運んでいます")
        self.assertEqual(parsed["robots"][0]["table"], 1)
        self.assertEqual(parsed["people"][0]["state"], "食事中")
        self.assertEqual([s["id"] for s in parsed["seats"]], ["Chair 1-1-0"])
        self.assertEqual(parsed["talk"]["n"], 1)
        hub = Hub(Monitor(learn_seconds=0), log=lambda m: None)
        self.assertTrue(hub.on_line(line(message), 0.0))
        self.assertTrue(hub.on_line(line({**message, "seq": 2}), 0.5))   # 同じ会話は 1 回だけ記録する
        talks = [r for r in hub.records if r["kind"] == "talk"]
        self.assertEqual(len(talks), 1)
        self.assertEqual(talks[0]["answer"], "スープを運んでいます")


class ProtocolTest(unittest.TestCase):
    def test_unity_line_with_nan_is_rejected_whole_by_strict_json(self):
        # NaN はそもそも JSON ではない: Python の json は既定で通してしまうが、ここでは行ごと捨てる
        with self.assertRaises(Rejected):
            decode(UNITY_LINE)

    def test_unity_line_without_nan_keeps_good_robots_and_drops_bad_values(self):
        cleaned = UNITY_LINE.replace('"x":NaN', '"x":-4.2').replace('"speed":Infinity', '"speed":0.5')
        parsed = parse_telemetry(decode(cleaned.encode("utf-8")))
        self.assertEqual([r["id"] for r in parsed["robots"]], ["A", "B", "C"])
        c = parsed["robots"][2]
        self.assertEqual(c["condition"], "Normal")      # 知らない状態は正常扱い（安全側の判定は Unity が持つ）
        self.assertIsNone(c["vibration"])               # 範囲外は「値なし」
        self.assertEqual(c["speed_scale"], 1.0)         # 範囲外は既定値
        self.assertEqual(parsed["people"][0]["role"], "floor")

    def test_robot_without_position_is_skipped_not_fatal(self):
        message = telemetry(1, [robot("A"), {"id": "B", "x": None, "z": 0}])
        self.assertEqual([r["id"] for r in parse_telemetry(message)["robots"]], ["A"])

    def test_wrong_version_type_or_no_robots_are_rejected(self):
        for bad in ({**telemetry(1, [robot("A")]), "v": 2}, {**telemetry(1, [robot("A")]), "type": "advice"},
                    telemetry(1, []), {**telemetry(1, [robot("A")]), "seq": "1"}):
            with self.assertRaises(Rejected):
                parse_telemetry(bad)

    def test_garbage_and_huge_lines_are_rejected(self):
        for bad in (b"hello", b"[1,2]", b"{not json", b"{" + b" " * 20000 + b"}", "﻿\r\n"):
            with self.assertRaises(Rejected):
                decode(bad)

    def test_advice_is_utf8_json_without_nan(self):
        data = encode({"reason": "通路中で揺れた", "x": 1.5})
        self.assertTrue(data.endswith(b"\n"))
        self.assertIn("通路中".encode("utf-8"), data)   # \u エスケープにしない（Unity にそのまま渡す）
        self.assertEqual(data.count(b"\n"), 1)
        self.assertEqual(json.loads(data)["reason"], "通路中で揺れた")
        with self.assertRaises(ValueError):
            encode({"x": math.nan})


class MonitorTest(unittest.TestCase):
    def run_steps(self, monitor, frames, start=0.0, step=0.5):
        now = start
        for robots in frames:
            monitor.observe(parse_telemetry(telemetry(int(now * 2) + 1, robots)), now)
            now += step
        return now

    def learned(self, **kwargs):
        monitor = Monitor(learn_seconds=5, **kwargs)
        now = self.run_steps(monitor, [[robot("A"), robot("B"), robot("C")]] * 12)
        self.assertFalse(monitor.learning(now))
        return monitor, now

    def test_two_robots_shaking_in_one_place_slow_that_place(self):
        monitor, now = self.learned()
        now = self.run_steps(monitor, [[robot("A", "通路中", 0.6), robot("B"), robot("C")]] * 3, now)
        self.assertEqual(monitor.area_scale, {})
        self.run_steps(monitor, [[robot("A", "通路北"), robot("B"), robot("C", "通路中", 0.6)]] * 3, now)
        self.assertEqual(list(monitor.area_scale), ["通路中"])
        self.assertEqual(monitor.area_scale["通路中"]["speed_scale"], AREA_SCALE)
        self.assertEqual(monitor.robot_scale, {})
        robots, zones, _ = monitor.advice()
        self.assertEqual(zones[0]["name"], "通路中")
        self.assertIn("AとC", zones[0]["reason"])

    def test_one_robot_shaking_everywhere_is_the_machine(self):
        monitor, now = self.learned()
        frames = [[robot("A"), robot("B", node, 0.5), robot("C")] for node in ("待機場所",) * 3 + ("通路中",) * 3 + ("通路北",) * 3]
        self.run_steps(monitor, frames, now)
        self.assertEqual(list(monitor.robot_scale), ["B"])
        self.assertEqual(monitor.robot_scale["B"]["speed_scale"], ROBOT_SCALE)
        self.assertEqual(monitor.area_scale, {})

    def test_machine_robot_does_not_make_a_place_look_bad(self):
        monitor, now = self.learned()
        frames = [[robot("A"), robot("B", node, 0.5), robot("C")] for node in ("待機場所", "通路中", "通路北") * 2]
        now = self.run_steps(monitor, frames, now)
        self.run_steps(monitor, [[robot("A", "通路中", 0.6), robot("B", "通路中", 0.5), robot("C")]] * 2, now)
        self.assertNotIn("通路中", monitor.area_scale)   # B は揺れを持ち込んでいるだけ

    def test_relax_is_only_a_proposal_until_a_person_approves(self):
        monitor, now = self.learned(relax_after=10)
        now = self.run_steps(monitor, [[robot("A", "通路中", 0.6), robot("B", "通路中", 0.6), robot("C")]] * 2, now)
        now = self.run_steps(monitor, [[robot("A"), robot("B"), robot("C")]] * 24, now)
        _, zones, proposals = monitor.advice()
        self.assertEqual(len(zones), 1)                  # まだ下げたまま
        self.assertEqual(len(proposals), 1)
        proposal = proposals[0]["id"]
        monitor.observe(parse_telemetry(telemetry(999, [robot("A")], approved=[proposal])), now)
        self.assertEqual(monitor.advice()[1], [])       # 人が承認して初めて戻る

    def test_declined_proposal_is_not_repeated_at_once(self):
        monitor, now = self.learned(relax_after=10)
        now = self.run_steps(monitor, [[robot("A", "通路中", 0.6), robot("B", "通路中", 0.6), robot("C")]] * 2, now)
        now = self.run_steps(monitor, [[robot("A"), robot("B"), robot("C")]] * 24, now)
        proposal = monitor.advice()[2][0]["id"]
        monitor.observe(parse_telemetry(telemetry(999, [robot("A")], declined=[proposal])), now)
        self.run_steps(monitor, [[robot("A"), robot("B"), robot("C")]] * 4, now)
        self.assertEqual(monitor.advice()[2], [])
        self.assertEqual(len(monitor.advice()[1]), 1)

    def test_first_reading_on_a_bump_is_not_learned_as_normal(self):
        monitor = Monitor(learn_seconds=1, simulated_bumps=("通路中",))
        now = self.run_steps(monitor, [[robot("A", "通路中"), robot("B", "通路中")]] + [[robot("A"), robot("B")]] * 3, 0.0)
        self.run_steps(monitor, [[robot("A", "通路中"), robot("B", "通路中")]] * 2, now + 1)
        self.assertIn("通路中", monitor.area_scale)

    def test_person_can_restore_without_a_proposal(self):
        monitor, now = self.learned()
        now = self.run_steps(monitor, [[robot("A", "通路中", 0.6), robot("B", "通路中", 0.6), robot("C")]] * 2, now)
        self.assertIn("通路中", monitor.area_scale)
        monitor.observe(parse_telemetry(telemetry(999, [robot("A")], restored=["zone:通路中", "bogus"])), now)
        self.assertEqual(monitor.area_scale, {})
        self.run_steps(monitor, [[robot("A"), robot("B"), robot("C")]] * 2, now + 0.5)
        self.assertEqual(monitor.area_scale, {})   # 古い揺れで下げ直さない

    def test_nothing_happens_while_learning(self):
        monitor = Monitor(learn_seconds=30)
        self.run_steps(monitor, [[robot("A", "通路中", 0.9), robot("B", "通路中", 0.9)]] * 10)
        self.assertEqual((monitor.area_scale, monitor.robot_scale), ({}, {}))

    def test_simulated_bump_is_labelled(self):
        monitor = Monitor(learn_seconds=5, simulated_bumps=("通路南",))
        now = self.run_steps(monitor, [[robot("A"), robot("B"), robot("C")]] * 12)
        self.run_steps(monitor, [[robot("A", "通路南"), robot("B", "通路南"), robot("C")]] * 2, now)
        self.assertIn("模擬", monitor.area_scale["通路南"]["reason"])


class DispatchTest(unittest.TestCase):
    def test_shortest_path_follows_the_aisle(self):
        seconds, route = shortest_path("返却台", "テーブル1")
        self.assertEqual(route[0], "返却台")
        self.assertEqual(route[-1], "テーブル1")
        self.assertTrue(math.isfinite(seconds))
        direct = sum(math.dist(NODES[a], NODES[b]) for a, b in zip(route, route[1:])) / 1.2
        self.assertAlmostEqual(seconds, direct, places=6)

    def test_hazard_makes_the_route_go_round_or_slower(self):
        plain, _ = shortest_path("厨房", "テーブル1")
        slowed, _ = shortest_path("厨房", "テーブル1", {"通路中"})
        self.assertGreaterEqual(slowed, plain)

    def test_stopped_robot_calls_maintenance_first(self):
        x, z = NODES["テーブル2"]
        choice = recommend(Failure("B", "stopped", x, z, 0.0), [])
        self.assertEqual((choice["role"], choice["person"], choice["backup"]), ("保守担当", "保守担当", "管理者"))
        self.assertEqual(choice["route"][-1], "テーブル2")

    def test_stuck_robot_calls_the_nearest_free_floor_staff(self):
        x, z = NODES["テーブル1"]
        people = [{"id": "staff", "role": "floor", "x": NODES["入口"][0], "z": NODES["入口"][1], "busy": False}]
        choice = recommend(Failure("A", "stuck", x, z, 0.0), people)
        self.assertEqual(choice["person"], "フロア店員")
        self.assertEqual(choice["route"], ["入口", "通路南", "テーブル1"] if choice["route"][1] == "通路南" else choice["route"])

    def test_busy_staff_arrive_later(self):
        x, z = NODES["テーブル1"]
        free = recommend(Failure("A", "stuck", x, z, 0.0), [{"id": "s", "role": "floor", "x": 1.5, "z": -5.3, "busy": False}])
        busy = recommend(Failure("A", "stuck", x, z, 0.0), [{"id": "s", "role": "floor", "x": 1.5, "z": -5.3, "busy": True}])
        self.assertAlmostEqual(busy["eta_s"] - free["eta_s"], 30.0, places=1)

    def test_nearest_node(self):
        self.assertEqual(nearest_node(3.5, 0.2), "テーブル2")


class HubTest(unittest.TestCase):
    def test_bad_lines_are_counted_and_replays_ignored(self):
        messages = []
        hub = Hub(Monitor(learn_seconds=0), messages.append)
        self.assertTrue(hub.on_line(line(telemetry(1, [robot("A")])), 0.0))
        self.assertFalse(hub.on_line(b"{broken", 0.1))
        self.assertFalse(hub.on_line(line(telemetry(1, [robot("A")])), 0.2))   # 同じ seq の再送
        self.assertTrue(hub.on_line(line(telemetry(2, [robot("A")])), 0.3))
        self.assertEqual(hub.rejected, 2)
        self.assertTrue(hub.on_line(line(telemetry(1, [robot("A")], boot="unity-restarted")), 0.4))  # Unity を起動し直した

    def test_failed_robot_gets_a_recommendation_in_the_advice(self):
        hub = Hub(Monitor(learn_seconds=0), lambda message: None)
        hub.on_line(line(telemetry(1, [robot("A"), robot("B", "テーブル2", stopped=True)])), 0.0)
        advice = json.loads(hub.advice(0.0))
        self.assertEqual(advice["type"], "advice")
        self.assertEqual(advice["dispatch"][0]["robot"], "B")
        self.assertEqual(advice["dispatch"][0]["person"], "保守担当")
        hub.on_line(line(telemetry(2, [robot("A"), robot("B", "テーブル2")])), 1.0)
        self.assertEqual(json.loads(hub.advice(1.0))["dispatch"], [])

    def test_robot_stuck_with_cargo_is_detected_after_30_seconds(self):
        hub = Hub(Monitor(learn_seconds=0), lambda message: None)
        people = [{"id": "staff", "role": "floor", "x": 1.6, "z": -4.3, "busy": False}]
        for i in range(70):
            hub.on_line(line(telemetry(i + 1, [robot("A", "テーブル1", carrying=True)], people)), i * 0.5)
        self.assertEqual(hub.dispatch[0]["situation"], "荷物を載せたまま動けない")
        self.assertEqual(hub.dispatch[0]["person"], "フロア店員")

    def test_robot_released_after_a_long_stop_is_not_called_stuck(self):
        hub = Hub(Monitor(learn_seconds=0), lambda message: None)
        for i in range(120):                                              # 60 秒止まっていた
            hub.on_line(line(telemetry(i + 1, [robot("C", "テーブル3", carrying=True, stopped=True)])), i * 0.5)
        hub.on_line(line(telemetry(121, [robot("C", "テーブル3", carrying=True)])), 60.0)   # 止めを解いた直後（まだ速度 0）
        self.assertEqual(hub.dispatch, [])

    def test_drill_lines_are_each_unusable_for_unity(self):
        hub = Hub(Monitor(learn_seconds=0), log=lambda _: None)
        hub.on_line(line(telemetry(1, [robot("B")])), 0.0)
        hub.advice(0.5)
        before = hub.seq
        raise_speed, stale, broken = hub.drill_lines()
        first = json.loads(raise_speed)
        self.assertEqual(first["seq"], before + 1)
        self.assertEqual(first["robots"][0]["id"], "B")             # Unity にいるロボットでないと、黙って読み飛ばされる
        self.assertGreater(first["robots"][0]["speed_scale"], 1)    # 速度を上げる指示
        self.assertLess(json.loads(stale)["seq"], first["seq"])     # 順番が戻った
        with self.assertRaises(ValueError):
            json.loads(broken)                                       # 途中で切れた JSON
        self.assertTrue(all(line.endswith(b"\n") for line in (raise_speed, stale, broken)))
        self.assertEqual(hub.records[-1]["kind"], "drill")
        self.assertEqual(json.loads(hub.advice(1.0))["seq"], before + 2)   # ふだんの助言は順番どおり続く

    def test_export_writes_records_and_clears_them(self):
        hub = Hub(Monitor(learn_seconds=0), lambda message: None)
        hub.on_line(line(telemetry(1, [robot("B", "テーブル2", stopped=True)])), 0.0)
        with tempfile.TemporaryDirectory() as name:
            path = hub.export(Path(name))
            text = path.read_text(encoding="utf-8")
            self.assertIn("保守担当", text)
            self.assertEqual(hub.records, [])
            self.assertEqual(len(list(Path(name).glob("guardian-records-*.jsonl"))), 1)
            self.assertEqual(list(Path(name).glob(".*.tmp")), [])


class Door2Test(unittest.TestCase):
    """扉 2 の外とのやりとり（Gemini の返事の読み取り、Bluesky の共有文）。通信はしない。"""

    snapshot = {"slowdowns": [{"kind": "area", "name": "通路中", "scale": 0.6, "reason": "通路中でAとBが揺れた（模擬の段差）"}],
                "robots": [{"id": "C", "condition": "Normal", "stopped": True}],
                "dispatch": [{"robot": "C", "situation": "停止（保持）", "person": "保守担当", "eta_s": 11.7, "backup": "管理者"}],
                "records": []}

    def test_key_goes_in_the_header_not_the_url(self):
        seen = {}

        def transport(url, body, headers):
            seen.update(url=url, headers=headers, body=body)
            return {"candidates": [{"content": {"parts": [{"text": "考え中", "thought": True},
                                                          {"text": '```json\n{"assessment": "段差", "recommendation": "確かめる", "confidence": "中"}\n```'}]}}]}

        judgement = GeminiClient("secret-key", transport=transport).judge("要約")
        self.assertNotIn("secret-key", seen["url"])
        self.assertEqual(seen["headers"], {"x-goog-api-key": "secret-key"})
        self.assertEqual(judgement, {"assessment": "段差", "recommendation": "確かめる", "confidence": "中"})

    def test_plain_text_and_blocked_answers(self):
        plain = parse_judgement({"candidates": [{"content": {"parts": [{"text": "床を\n確かめて\x07ください"}]}}]})
        self.assertEqual(plain["assessment"], "床を 確かめて ください")
        with self.assertRaises(GeminiError):
            parse_judgement({"promptFeedback": {"blockReason": "SAFETY"}})
        with self.assertRaises(GeminiError):
            parse_judgement({"candidates": [{"content": {"parts": [{"text": "{}"}]}}]})

    def test_post_fits_bluesky_and_marks_the_simulated_bump(self):
        text = compose_post(self.snapshot, ["ロボもり", "JPHACKS"])
        self.assertLessEqual(len(text), 300)
        self.assertIn("（模擬の段差）", text)
        self.assertIn("#ロボもり", text)
        crowded = dict(self.snapshot, slowdowns=self.snapshot["slowdowns"] * 30)
        self.assertIn("ほか", compose_post(crowded, ["ロボもり"]))
        self.assertLessEqual(len(compose_post(crowded, ["ロボもり"])), 300)

    def test_nothing_notable_means_nothing_is_sent(self):
        class Never:
            def judge(self, situation):
                raise AssertionError("聞いてはいけない")

        quiet = {"slowdowns": [], "robots": [{"id": "A", "condition": "Normal", "stopped": False}], "dispatch": [], "records": []}
        result = Outside(Never(), None, ["ロボもり"]).exchange(quiet)
        self.assertFalse(result["notable"])
        self.assertIsNone(result["judgement"])


STORE = "did:plc:store0000000000000000000"
FRIEND = "did:plc:friend000000000000000000"
STRANGER = "did:plc:stranger0000000000000000"


class FakeBluesky:
    """Bluesky の代わり。投稿・記録・返信・フォロー一覧・ハンドルの解決を、通信せずに返す。"""

    def __init__(self, replies=(), follows=(FRIEND,), handles=None):
        self.did, self.handle = STORE, "robomori.test"
        self._replies = list(replies)
        self._follows = set(follows)
        self._handles = handles or {}
        self.posts, self.records, self.asked = [], [], []

    def post(self, text, facets=None):
        self.posts.append(text)
        n = len(self.posts)
        return {"uri": f"at://{STORE}/app.bsky.feed.post/p{n}", "cid": f"bafypost{n}", "url": f"https://bsky.app/profile/robomori.test/post/p{n}",
                "handle": self.handle, "created_at": "2026-10-25T01:00:00.000Z"}

    def create_record(self, collection, record):
        self.records.append((collection, record))
        n = len(self.records)
        return {"uri": f"at://{STORE}/{collection}/r{n}", "cid": f"bafyrecord{n}"}

    def replies(self, uri):
        return list(self._replies)

    def follows(self, actor):
        self.asked.append(("follows", actor))
        return set(self._follows)

    def resolve_handle(self, handle):
        self.asked.append(("resolve", handle))
        if handle not in self._handles:
            raise ValueError("見つからない")
        return self._handles[handle]


def reply_from(did, rkey, text="段差の固定を見て", at="2026-10-25T02:00:00.000Z", handle=""):
    return {"uri": f"at://{did}/app.bsky.feed.post/{rkey}", "cid": f"bafy{rkey}", "did": did, "handle": handle or rkey + ".test",
            "text": text, "url": "", "created_at": at, "indexed_at": at}


class AtprotoShareTest(unittest.TestCase):
    """店が選んだ出し方と、受け取る相手（DID）・受付期限。記録は前の記録と、受け取った意見を参照する。"""

    snapshot = {"slowdowns": [{"kind": "area", "name": "通路", "scale": 0.6, "reason": "複数台が揺れた"}],
                "robots": [{"id": "A", "condition": "Warning", "stopped": False}], "dispatch": [], "records": []}

    def outside(self, bluesky, **policy):
        outside = Outside(None, bluesky, ["ロボもり"], "デモ店", {"share": "post", **policy})
        outside.posts = [{"uri": f"at://{STORE}/app.bsky.feed.post/p0", "url": "", "handle": "robomori.test",
                          "created_at": "2026-10-25T00:00:00+09:00"}]
        return outside

    def test_unknown_policy_values_fall_to_the_safe_side(self):
        policy = normalize_policy({"share": "everything", "accept": "?", "gemini": "yes", "window_days": 999,
                                   "allow": ["@alice.test", "did:plc:abc", "not a handle", 3]})
        self.assertEqual(policy, {"share": "off", "gemini": False, "accept": "listed", "allow": ["alice.test", "did:plc:abc"], "window_days": 30,
                                  "recipients": []})

    def test_only_followed_accounts_are_taken_and_others_leave_no_text(self):
        bluesky = FakeBluesky(replies=[reply_from(FRIEND, "f1"), reply_from(STRANGER, "s1", text="宣伝です"),
                                       reply_from(STORE, "own", text="店からの返信")])
        outside = self.outside(bluesky)
        result = outside.exchange(self.snapshot)
        self.assertEqual([r["did"] for r in result["replies"]], [FRIEND])
        self.assertEqual(result["not_allowed"], 1)
        self.assertIn(("follows", STORE), bluesky.asked)
        record = bluesky.records[0][1]
        self.assertEqual(record["opinions"], [{"author": FRIEND, "post": {"uri": f"at://{FRIEND}/app.bsky.feed.post/f1", "cid": "bafyf1"}}])
        self.assertEqual((record["share"], record["accept"]), ("post", "following"))
        self.assertEqual(record["post"]["uri"], f"at://{STORE}/app.bsky.feed.post/p1")
        self.assertNotIn("宣伝です", json.dumps(result, ensure_ascii=False) + render_report("x", [], result))
        self.assertEqual(outside.exchange(self.snapshot)["replies"], [], "同じ返信は 2 回受け取らない")

    def test_listed_people_are_matched_by_did_after_resolving_handles(self):
        bluesky = FakeBluesky(replies=[reply_from(FRIEND, "f1", handle="friend.test"), reply_from(STRANGER, "s1", handle="stranger.test")],
                              handles={"friend.test": FRIEND})
        result = self.outside(bluesky, accept="listed", allow=["friend.test", "missing.test"]).exchange(self.snapshot)
        self.assertEqual([r["did"] for r in result["replies"]], [FRIEND])
        self.assertTrue(any("missing.test" in e for e in result["errors"]), result["errors"])

    def test_late_replies_are_not_taken(self):
        bluesky = FakeBluesky(replies=[reply_from(FRIEND, "late", at="2026-11-10T00:00:00.000Z"), reply_from(FRIEND, "nodate", at="")])
        result = self.outside(bluesky, accept="anyone", window_days=7).exchange(self.snapshot)
        self.assertEqual((result["replies"], result["late"]), ([], 2))

    def test_follow_list_failure_takes_no_one(self):
        class Broken(FakeBluesky):
            def follows(self, actor):
                raise OSError("つながらない")
        result = self.outside(Broken(replies=[reply_from(FRIEND, "f1")])).exchange(self.snapshot)
        self.assertEqual(result["replies"], [])
        self.assertTrue(any("フォロー一覧" in e for e in result["errors"]))

    def test_share_off_writes_nothing_and_record_only_skips_the_timeline(self):
        silent = FakeBluesky()
        result = self.outside(silent, share="off").exchange(self.snapshot)
        self.assertEqual((silent.posts, silent.records, result["post"], result["record"]), ([], [], None, None))
        self.assertIn("共有しない", render_report("x", [], result))
        quiet = FakeBluesky()
        result = self.outside(quiet, share="record").exchange(self.snapshot)
        self.assertEqual(quiet.posts, [])
        self.assertEqual(quiet.records[0][1]["share"], "record")
        self.assertNotIn("post", quiet.records[0][1])
        self.assertIn("タイムラインには投稿していない", render_report("x", [], result))

    def test_records_chain_and_carry_opinions_and_hold_no_floats(self):
        bluesky = FakeBluesky()
        outside = self.outside(bluesky)
        outside.previous = {"uri": f"at://{STORE}/internal.robomori.report/r0", "cid": "bafyrecord0"}
        outside.carry = [{"author": FRIEND, "post": {"uri": f"at://{FRIEND}/app.bsky.feed.post/old", "cid": "bafyold"}},
                         {"author": "not-a-did", "post": {"uri": "at://x/y/z", "cid": "c"}}]
        outside.exchange(self.snapshot)
        outside.exchange(self.snapshot)
        first, second = bluesky.records[0][1], bluesky.records[1][1]
        self.assertEqual(first["previous"]["cid"], "bafyrecord0")
        self.assertEqual([o["post"]["cid"] for o in first["opinions"]], ["bafyold"])
        self.assertEqual(second["previous"], {"uri": f"at://{STORE}/internal.robomori.report/r1", "cid": "bafyrecord1"})
        self.assertNotIn("opinions", second)

        def floats(value):
            if isinstance(value, float):
                return True
            if isinstance(value, dict):
                return any(floats(v) for v in value.values())
            return isinstance(value, list) and any(floats(v) for v in value)
        self.assertFalse(floats(first), "AT プロトコルのレコードに小数は入れられない")
        self.assertEqual(first["slowdowns"][0]["speedPercent"], 60)

    def test_record_strings_fit_the_lexicon_in_utf8_bytes(self):
        from door2 import report_record
        long = {"slowdowns": [{"kind": "area", "name": "とても長い場所の名前" * 10, "scale": 0.25, "reason": "日本語の長い理由" * 100}],
                "robots": [{"id": "ロボットABC", "condition": "Limited", "stopped": True}],
                "dispatch": [{"robot": "C", "situation": "停止（保持）" * 20, "person": "保守担当" * 20, "eta_s": -1.0}], "records": []}
        record = report_record(long, "要約" * 200, None, "店" * 100)
        size = lambda text: len(text.encode("utf-8"))
        self.assertLessEqual(size(record["slowdowns"][0]["place"]), 64)
        self.assertLessEqual(size(record["slowdowns"][0]["reason"]), 600)
        self.assertEqual(record["slowdowns"][0]["speedPercent"], 30, "lexicon の最小 30 に収める")
        self.assertLessEqual(size(record["responders"][0]["person"]), 64)
        self.assertEqual(record["responders"][0]["etaSeconds"], 0)
        self.assertLessEqual(size(record["stopped"][0]), 8)
        self.assertLessEqual(len(record["store"]), 64)
        self.assertLessEqual(len(record["summary"]), 300)

    def test_gemini_can_be_turned_off(self):
        class Never:
            def judge(self, situation):
                raise AssertionError("聞いてはいけない")
        result = Outside(Never(), None, ["ロボもり"], policy={"gemini": False}).exchange(self.snapshot)
        self.assertEqual((result["gemini"], result["judgement"]), ("off", None))


class SealedShareTest(unittest.TestCase):
    """会社だけ: 平文は外に出さず、宛先の人の鍵でしか開けない記録を 1 件置く。"""

    def test_only_the_recipients_can_open_and_nothing_plain_goes_out(self):
        from cryptography.hazmat.primitives.asymmetric import ec
        from seal import open_sealed, public_of
        alice, bob, eve = (ec.generate_private_key(ec.SECP256R1()) for _ in range(3))
        bluesky = FakeBluesky()
        outside = Outside(None, bluesky, ["ロボもり"], "デモ店", {"recipients": [{"name": "店長", "key": public_of(alice)},
                                                                         {"name": "整備", "key": public_of(bob)},
                                                                         {"name": "壊れた", "key": "robomori-key:1:nope"}]})
        result = outside.exchange(AtprotoShareTest.snapshot)
        self.assertEqual(bluesky.posts, [], "会社だけのときは投稿しない")
        _, record = bluesky.records[0]
        self.assertEqual((record["share"], len(record["sealed"]["recipients"]), result["record"]["recipients"]), ("sealed", 2, 2))
        public = json.dumps(record, ensure_ascii=False)
        self.assertNotIn("通路", public)
        self.assertNotIn("デモ店", public)
        self.assertEqual(len(base64.urlsafe_b64decode(record["sealed"]["ciphertext"] + "==")) % 1024, 16, "長さは 1024 単位（＋認証の 16）")
        for key in (alice, bob):
            opened = open_sealed(record["sealed"], key)
            self.assertEqual(opened["slowdowns"][0]["place"], "通路")
            self.assertEqual(opened["store"], "デモ店")
        with self.assertRaises(StopIteration):
            open_sealed(record["sealed"], eve)

    def test_no_recipients_writes_nothing(self):
        bluesky = FakeBluesky()
        result = Outside(None, bluesky, ["ロボもり"], "デモ店", {"share": "sealed"}).exchange(AtprotoShareTest.snapshot)
        self.assertEqual(bluesky.records, [])
        self.assertTrue(any("宛先" in e for e in result["errors"]))


class RuleJudgementShareTest(unittest.TestCase):
    """扉 2 の共有と記録に、ルール AI の判断と M5 の実測を入れる。数は整数だけ。"""

    judgement = {"rulesVersion": "abc123def456", "windowMinutes": 30, "store": "caution",
                 "robots": [{"robot": "A", "level": "safe", "stops": 0, "degraded": 0},
                            {"robot": "B", "level": "danger", "stops": 4, "degraded": 2}],
                 "sensor": {"device": "M5StickS3", "robot": "B", "thresholdMg": 300, "peakMg": 1898, "raised": 2,
                            "humanRecoveries": 2, "gaps": 0}}
    snapshot = {"slowdowns": [], "robots": [{"id": "B", "condition": "Warning", "stopped": False}], "dispatch": [], "records": [],
                "judgement": judgement}

    def test_judgement_alone_is_worth_sharing_and_reads_in_the_post(self):
        from door2 import notable
        self.assertTrue(notable(dict(self.snapshot, robots=[])))
        text = compose_post(self.snapshot, ["ロボもり"])
        self.assertIn("ロボットB：ルールAIの判断は危険（30分で停止4回・注意/制限2回）", text)
        self.assertIn("M5StickS3の実測 最大1898mg（しきい値300mg）・注意以上2回・人がKEY2で2回戻した", text)
        self.assertNotIn("ロボットB：注意", text, "同じロボットを 2 回書かない")
        self.assertLessEqual(len(text), 300)

    def test_record_carries_the_judgement_and_the_measurement(self):
        from door2 import report_record
        record = report_record(self.snapshot, "要約", None, "店")
        self.assertEqual(record["judgement"]["rulesVersion"], "abc123def456")
        self.assertEqual(record["judgement"]["robots"][1], {"robot": "B", "level": "danger", "stops": 4, "degraded": 2})
        self.assertEqual(record["sensor"], {"device": "M5StickS3", "robot": "B", "measured": True, "thresholdMg": 300,
                                            "peakMg": 1898, "raised": 2, "humanRecoveries": 2, "gaps": 0})
        bad = dict(self.snapshot, judgement=dict(self.judgement, store="?", robots=[{"robot": "B", "level": "x", "stops": 2.7}]))
        self.assertEqual(report_record(bad, "要約", None, "店")["judgement"]["robots"][0], {"robot": "B", "level": "safe", "stops": 2, "degraded": 0})
        quiet = dict(self.snapshot, judgement=None)
        self.assertNotIn("judgement", report_record(quiet, "要約", None, "店"))


if __name__ == "__main__":
    unittest.main()
