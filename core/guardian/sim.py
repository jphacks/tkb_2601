"""Unity の代役。3 台のロボットの様子を Unity（GuardianHubLink）と同じ形で hub に送り、助言を Unity と同じ決まりで受ける。

Unity が落ちたとき・用意できないときに、監視 AI と画面をそのまま見せるためのもの。送るのは模擬のデータで、
boot を "sim-…" にして画面に「代役」と出す。人が押すところ（提案の承認、扉 1 を開ける）は、代役が時間で行う。
"""
from __future__ import annotations

import json
import math
import random
import time
import uuid

from layout import CENTRAL_AISLE_X, GUEST_AISLES, NODES, STAFF_HOME, as_scene

BUMP_AT = NODES["通路中"]    # 中央の通路のまん中。ここを通ると揺れる（模擬の段差）
BUMP_RADIUS = 1.3
BUMP_VIBRATION = 0.35
SPEED = 1.4                 # m/s
CYCLE = 150.0               # C が止まって戻るまでの周期（秒）
STOP_WINDOW = (50.0, 110.0)
APPROVE_AFTER = 20.0        # 提案を代役が承認するまでの時間（Unity では人が押す）
REOPEN_AFTER = 15.0         # 扉 1 が閉じてから代役が開け直すまで（Unity では人が押す）
RESTORE_GRACE = 5.0



def unity_route(src, dst):
    """Unity と同じ道順（GuardianTrafficController の BuildRoute）: 手前の待ち位置 → 中央の通路 x=-1 → 先の待ち位置 → 行き先。"""
    hold = lambda p: (-3.0 if p[0] < -1 else 1.0, p[1])
    points = [src]
    if abs(src[0] - CENTRAL_AISLE_X) >= 1.2:
        points += [hold(src), (CENTRAL_AISLE_X, src[1])]
    return points + [(CENTRAL_AISLE_X, dst[1]), hold(dst), dst]


def round_trip(a, b):
    return unity_route(a, b)[:-1] + unity_route(b, a)[:-1]


# Unity の最初の 3 件の仕事と同じ（S-101 スープ 厨房→テーブル1、F-102 料理 厨房→テーブル2、R-103 皿 テーブル3→返却台）
PATHS = {
    "A": round_trip(NODES["厨房"], NODES["テーブル1"]),
    "B": round_trip(NODES["厨房"], NODES["テーブル2"]),
    "C": round_trip(NODES["テーブル3"], NODES["返却台"]),
}
TASKS = {"A": "Soup", "B": "Food", "C": "EmptyDishes"}
TABLE_OF = {"A": 1, "B": 2, "C": 3}
# 代役の「今の状況は？」への答え（Unity では RobomoriTalk がロボット自身の状態から作る）
SAYS = {"A": "（代役）テーブル1 へスープを運んでいます", "B": "（代役）テーブル2 へ料理を運んでいます",
        "C": "（代役）テーブル3 のお皿を返却台へ運んでいます"}
AISLE_HALF_WIDTH = 0.6
# フロア店員: 待機場所から客の通り道（南の通路 → 東の通路）を通って席へ案内し、戻る（GuardianDinerFlow）
_SOUTH, _EAST = GUEST_AISLES["south_z"], GUEST_AISLES["east_x"]
STAFF_PATH = [STAFF_HOME, (STAFF_HOME[0], _SOUTH), (_EAST, _SOUTH), (_EAST, 0.0), (_EAST, _SOUTH), (STAFF_HOME[0], _SOUTH)]


def _in_aisle(x: float) -> bool:
    return abs(x - CENTRAL_AISLE_X) < AISLE_HALF_WIDTH


def _length(path) -> float:
    closed = path + [path[0]]
    return sum(math.dist(a, b) for a, b in zip(closed, closed[1:]))


def _loop_point(path, distance):
    closed = path + [path[0]]
    lengths = [math.dist(closed[i], closed[i + 1]) for i in range(len(closed) - 1)]
    distance %= sum(lengths)
    for (a, b), d in zip(zip(closed, closed[1:]), lengths):
        if distance <= d:
            t = distance / d if d else 0
            return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)
        distance -= d
    return closed[-1]


class StandIn:
    """ソケットを持たない代役の中身。tick() で様子の行を作り、on_advice() で助言の行を受ける。"""

    def __init__(self, seed: int = 2026):
        self.boot = "sim-" + uuid.uuid4().hex[:8]
        self.rng = random.Random(seed)
        self.seq = 0
        # 始めはばらけた場所から: A はテーブル1、B は厨房、C は返却台（それぞれ道のりの半分・始め・半分）
        self.distance = {robot: (_length(path) / 2 if robot != "B" else 0.0) for robot, path in PATHS.items()}
        self.stopped = False
        self.base = {"A": 0.09, "B": 0.11, "C": 0.08}
        self.robot_rules: dict[str, float] = {}
        self.zone_rules: dict[str, tuple[float, float, float, float]] = {}   # 名前 → (x, z, 半径, 倍率)
        self.proposals_seen: dict[str, float] = {}
        self.restored_at: dict[str, float] = {}
        self.decisions: list[tuple[str, str, int]] = []   # (approved/restored, 値, 残りの送信回数)
        self.hub_boot: str | None = None
        self.last_seq = -1
        self.rejections: list[float] = []
        self.closed_until = 0.0
        self.notes: list[str] = []

    # ---- 送る ----

    def scale_of(self, robot: str, x: float, z: float) -> float:
        scale = self.robot_rules.get(robot, 1.0)
        for zx, zz, radius, zone_scale in self.zone_rules.values():
            if math.dist((x, z), (zx, zz)) <= radius:
                scale = min(scale, zone_scale)
        return max(0.3, scale)

    def tick(self, t: float, dt: float) -> bytes:
        robots = []
        here = {robot: _loop_point(path, self.distance[robot]) for robot, path in PATHS.items()}
        for robot, path in PATHS.items():
            x, z = here[robot]
            phase = t % CYCLE
            # C は止まる。ただし中央の通路の中では止まらない（通路を出てから止まる）
            if robot == "C" and STOP_WINDOW[0] <= phase < STOP_WINDOW[1] and (self.stopped or not _in_aisle(x)):
                self.stopped = True
            elif not (STOP_WINDOW[0] <= phase < STOP_WINDOW[1]):
                self.stopped = False
            stopped = robot == "C" and self.stopped
            scale = self.scale_of(robot, x, z)
            speed = 0.0 if stopped else SPEED * scale
            # 中央の通路は 1 台ずつ（Unity の単線通路と同じ）。ほかのロボットがいれば、手前で待つ
            wait = "代役のため模擬" if stopped else ""
            ahead = _loop_point(path, self.distance[robot] + speed * dt)
            if speed and not _in_aisle(x) and _in_aisle(ahead[0]) and any(_in_aisle(p[0]) for r, p in here.items() if r != robot):
                speed, wait = 0.0, "中央の通路の順番待ち"
            self.distance[robot] += speed * dt
            here[robot] = _loop_point(path, self.distance[robot])
            vibration = self.base[robot] + self.rng.uniform(-0.02, 0.02)
            if math.dist((x, z), BUMP_AT) <= BUMP_RADIUS and speed:
                vibration += BUMP_VIBRATION
            robots.append({"id": robot, "x": round(x, 3), "z": round(z, 3), "speed": round(speed, 3),
                           "condition": "Normal", "stopped": stopped, "held": False,
                           "task": TASKS[robot], "phase": "ToDropoff", "table": TABLE_OF[robot],
                           "says": "（代役）止まっています。人が確かめるのを待っています" if stopped else SAYS[robot],
                           "carrying": robot == "C" or not stopped, "vibration": round(min(1.0, max(0.0, vibration)), 3),
                           "speed_scale": round(scale, 2), "wait": wait})
        sx, sz = _loop_point(STAFF_PATH, t * 0.8)
        self.seq += 1
        approved = [v for kind, v, _ in self.decisions if kind == "approved"]
        restored = [v for kind, v, _ in self.decisions if kind == "restored"]
        self.decisions = [(k, v, n - 1) for k, v, n in self.decisions if n > 1]
        message = {"v": 1, "type": "telemetry", "boot": self.boot, "door": "store", "seq": self.seq, "t": round(t, 2),
                   "robots": robots, "people": [{"id": "staff", "role": "floor", "x": round(sx, 3), "z": round(sz, 3), "busy": False}],
                   "approved": approved, "declined": [], "restored": restored}
        return (json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8")

    def scene_line(self) -> bytes:
        """つないだときに 1 回送る店内の配置（Unity は自分のシーンから読む。代役は layout.py の写しを使う）。"""
        scene = as_scene(self.boot)
        room = scene.pop("room")
        message = {"v": 1, "type": "scene", **scene, **room, "seq": 1}
        return (json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8")

    # ---- 受ける（Unity の GuardianHubLink と同じ決まり） ----

    def _reject(self, reason: str, now: float) -> None:
        self.rejections = [r for r in self.rejections if now - r <= 10] + [now]
        self.notes.append(f"使えない助言を捨てた: {reason}")
        if len(self.rejections) >= 3:
            self.closed_until = now + REOPEN_AFTER
            self.rejections.clear()
            self.notes.append(f"扉 1 を自動で閉じた（単独）。代役は {REOPEN_AFTER:.0f} 秒後に開け直す（Unity では人が押す）")

    def closed(self, now: float) -> bool:
        return now < self.closed_until

    def on_advice(self, raw: bytes, now: float) -> None:
        try:
            advice = json.loads(raw.decode("utf-8"))
        except ValueError:
            return self._reject("JSON として読めない", now)
        if not isinstance(advice, dict) or advice.get("v") != 1 or advice.get("type") != "advice" or not advice.get("boot"):
            return self._reject("ハブの助言の形ではない", now)
        if self.hub_boot is not None and advice["boot"] != self.hub_boot:
            return self._reject("同じ接続に別のハブの助言が混ざった", now)
        seq = advice.get("seq")
        if not isinstance(seq, int) or seq <= self.last_seq:
            return self._reject("古い助言（順番が戻った）", now)
        self.hub_boot, self.last_seq = advice["boot"], seq
        raised = False
        for rule in advice.get("robots") or []:
            scale = rule.get("speed_scale")
            if not isinstance(scale, (int, float)) or not 0 < scale < 1:
                raised = True
                continue
            key = "robot:" + str(rule.get("id"))
            if now - self.restored_at.get(key, -1e9) > RESTORE_GRACE:
                self.robot_rules[rule["id"]] = min(self.robot_rules.get(rule["id"], 1.0), max(0.3, scale))
        for zone in advice.get("zones") or []:
            scale = zone.get("speed_scale")
            if not isinstance(scale, (int, float)) or not 0 < scale < 1:
                raised = True
                continue
            key = "zone:" + str(zone.get("id"))
            if now - self.restored_at.get(key, -1e9) > RESTORE_GRACE and zone.get("id") not in self.zone_rules:
                self.zone_rules[zone["id"]] = (zone["x"], zone["z"], zone["r"], max(0.3, scale))
                self.notes.append(f"{zone['id']} の速度上限を ×{scale:g} にした（監視AI）")
        if raised:
            self._reject("速度を上げる、または範囲外の指示", now)
        for proposal in advice.get("proposals") or []:
            first = self.proposals_seen.setdefault(proposal["id"], now)
            if now - first >= APPROVE_AFTER:
                key = ("robot:" + proposal["robot"]) if proposal.get("robot") else ("zone:" + proposal.get("zone", ""))
                kind, name = key.split(":", 1)
                (self.robot_rules if kind == "robot" else self.zone_rules).pop(name, None)
                self.restored_at[key] = now
                self.decisions += [("approved", proposal["id"], 4), ("restored", key, 4)]
                self.proposals_seen.pop(proposal["id"], None)
                self.notes.append(f"代役が提案 {proposal['id']} を承認して、{name} を元の速度に戻した（Unity では人が押す）")
