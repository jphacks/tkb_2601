"""監視 AI: 全ロボットの動きを見て、その場で動き方を直す。インターネットは使わない。

- ロボットごとの「ふだんの揺れ」を、動かしながら学ぶ（指数移動平均）。
- 揺れた場所（店内の点）とロボットを記録し、複数台を比べる。
  - 同じ場所で 2 台以上が揺れた → 場所の問題（床の段差など）とみなし、その場所を通るときの速度を下げる。
  - 1 台だけが、場所を変えても揺れる → 機体の問題とみなし、その機体の速度を下げ、点検を勧める。
- 厳しくする側（速度を下げる）はすぐに出す。緩める側（元に戻す）は提案だけにし、Unity で人が承認したら戻す。
時刻は呼ぶ側が渡す（テストで同じ動きを再現できるように）。
"""
from __future__ import annotations

from collections import deque

from dispatch import NODES, nearest_node

ABSOLUTE = 0.30        # これ以上は、ふだんの値に関係なく「揺れた」（Unity の注意の基準と同じ）
MARGIN = 0.15          # ふだんの値よりこれだけ大きければ「揺れた」
ALPHA = 0.05           # ふだんの値を学ぶ速さ
AREA_SCALE = 0.6       # 場所の問題とみなした場所を通るときの速度の倍率
ROBOT_SCALE = 0.7      # 機体の問題とみなしたロボットの速度の倍率
AREA_RADIUS = 1.8      # 場所の広さ（点からの半径、m）
SIMULATED_BUMP = 0.35  # --simulate-bump の場所で足す揺れ（模擬）


class Monitor:
    def __init__(self, learn_seconds: float = 20.0, window: float = 60.0, relax_after: float = 120.0,
                 simulated_bumps: tuple[str, ...] = ()):
        self.learn_seconds = learn_seconds
        self.window = window
        self.relax_after = relax_after
        self.simulated_bumps = set(simulated_bumps)
        self.started: float | None = None
        self.baseline: dict[str, float] = {}
        self.anomalies: deque[tuple[float, str, str, bool]] = deque()   # (時刻, ロボット, 場所, 模擬か)
        self.last_robot_anomaly: dict[str, float] = {}
        self.last_area_anomaly: dict[str, float] = {}
        self.robot_scale: dict[str, dict] = {}   # 速度を下げているロボット
        self.area_scale: dict[str, dict] = {}    # 速度を下げている場所
        self.proposals: dict[str, dict] = {}     # 戻す提案（人の承認待ち）
        self.declined_until: dict[str, float] = {}
        self.conditions: dict[str, str] = {}
        self.events: list[str] = []
        self._next_proposal = 1

    def learning(self, now: float) -> bool:
        return self.started is None or now - self.started < self.learn_seconds

    def observe(self, telemetry: dict, now: float) -> None:
        """telemetry 1 回分を取り込み、必要なら速度を下げる。"""
        if self.started is None:
            self.started = now
        for decision in telemetry.get("approved", []):
            self._settle(decision, True, now)
        for decision in telemetry.get("declined", []):
            self._settle(decision, False, now)
        for key in telemetry.get("restored", []):
            self._restored(key)
        for robot in telemetry["robots"]:
            self.conditions[robot["id"]] = robot["condition"]
            value = robot["vibration"]
            if value is None:
                continue
            area = nearest_node(robot["x"], robot["z"])
            simulated = area in self.simulated_bumps
            if simulated:
                value = min(1.0, value + SIMULATED_BUMP)
            # 最初の 1 回が段差の上でも、それを「ふだん」にしない（揺れとみなす値より下から学び始める）
            base = self.baseline.setdefault(robot["id"], min(value, ABSOLUTE - MARGIN))
            shaking = value >= max(ABSOLUTE, base + MARGIN)
            if not shaking:
                self.baseline[robot["id"]] = base + ALPHA * (value - base)
            if shaking and not self.learning(now):
                self.anomalies.append((now, robot["id"], area, simulated))
                self.last_robot_anomaly[robot["id"]] = now
                self.last_area_anomaly[area] = now
        while self.anomalies and now - self.anomalies[0][0] > self.window:
            self.anomalies.popleft()
        if not self.learning(now):
            self._tighten(now)
            self._propose(now)

    def _tighten(self, now: float) -> None:
        by_robot: dict[str, set[str]] = {}
        for _, robot, area, _ in self.anomalies:
            by_robot.setdefault(robot, set()).add(area)
        # 場所を変えても揺れているロボットは、機体の問題。場所の判定には数えない（揺れを持ち込むだけなので）
        machine = {robot for robot, areas in by_robot.items() if len(areas) >= 2}
        for robot in sorted(machine - set(self.robot_scale)):
            reason = f"{robot}だけが場所を変えても揺れる（{'・'.join(sorted(by_robot[robot]))}）。機体の点検を"
            self.robot_scale[robot] = {"speed_scale": ROBOT_SCALE, "reason": reason, "since": now}
            self.events.append(f"機体の問題: ロボット {robot} の速度を {ROBOT_SCALE} 倍に下げた（{reason}）")
        by_area: dict[str, set[str]] = {}
        simulated_areas = set()
        for _, robot, area, simulated in self.anomalies:
            if robot in machine:
                continue
            by_area.setdefault(area, set()).add(robot)
            if simulated:
                simulated_areas.add(area)
        for area, robots in sorted(by_area.items()):
            if len(robots) >= 2 and area not in self.area_scale:
                note = "（模擬の段差）" if area in simulated_areas else ""
                reason = f"{area}で{'と'.join(sorted(robots))}が揺れた。床や段差の可能性{note}"
                self.area_scale[area] = {"speed_scale": AREA_SCALE, "reason": reason, "since": now}
                self.events.append(f"場所の問題: {area} の速度を {AREA_SCALE} 倍に下げた（{reason}）")

    def _propose(self, now: float) -> None:
        open_keys = {p["key"] for p in self.proposals.values()}
        for area, entry in self.area_scale.items():
            key = "area:" + area
            quiet = now - max(self.last_area_anomaly.get(area, entry["since"]), entry["since"])
            if quiet >= self.relax_after and key not in open_keys and now >= self.declined_until.get(key, 0):
                self._add_proposal(key, {"zone": area, "robot": ""}, f"{area}で {self.relax_after / 60:.0f} 分、揺れがない", now)
        for robot, entry in self.robot_scale.items():
            key = "robot:" + robot
            quiet = now - max(self.last_robot_anomaly.get(robot, entry["since"]), entry["since"])
            if (quiet >= self.relax_after and self.conditions.get(robot) == "Normal" and key not in open_keys
                    and now >= self.declined_until.get(key, 0)):
                self._add_proposal(key, {"zone": "", "robot": robot}, f"ロボット {robot} が {self.relax_after / 60:.0f} 分、揺れていない", now)

    def _add_proposal(self, key: str, target: dict, reason: str, now: float) -> None:
        proposal_id = f"p-{self._next_proposal}"
        self._next_proposal += 1
        self.proposals[proposal_id] = {"id": proposal_id, "key": key, "speed_scale": 1.0, "reason": reason, "created": now, **target}
        self.events.append(f"提案 {proposal_id}: {reason}。元の速度に戻すかを Unity で人が決める")

    def _settle(self, proposal_id: str, approved: bool, now: float) -> None:
        proposal = self.proposals.pop(proposal_id, None)
        if proposal is None:
            return
        if approved:
            # 戻す前の揺れの記録は、もう判定に使わない（残っていると、戻したとたんにまた下げてしまう）
            if proposal["zone"]:
                self.area_scale.pop(proposal["zone"], None)
                self.anomalies = deque(a for a in self.anomalies if a[2] != proposal["zone"])
            else:
                self.robot_scale.pop(proposal["robot"], None)
                self.anomalies = deque(a for a in self.anomalies if a[1] != proposal["robot"])
            self.events.append(f"承認 {proposal_id}: 人が元の速度に戻した")
        else:
            self.declined_until[proposal["key"]] = now + self.relax_after
            self.events.append(f"見送り {proposal_id}: 速度は下げたまま")

    def _restored(self, key: str) -> None:
        """提案を待たずに、人が Unity で減速を戻した。AI もそれに従い、同じ減速を出し直さない。"""
        kind, _, name = key.partition(":")
        table = self.area_scale if kind == "zone" else self.robot_scale
        if table.pop(name, None) is None:
            return
        column = 2 if kind == "zone" else 1
        self.anomalies = deque(a for a in self.anomalies if a[column] != name)
        for proposal_id in [p["id"] for p in self.proposals.values() if p["key"] == ("area:" if kind == "zone" else "robot:") + name]:
            del self.proposals[proposal_id]
        self.events.append(f"人が {name} の減速を戻した（提案を待たずに、人の判断で）")

    def hazards(self) -> set[str]:
        return set(self.area_scale)

    def advice(self) -> tuple[list[dict], list[dict], list[dict]]:
        robots = [{"id": robot, "speed_scale": e["speed_scale"], "reason": e["reason"]} for robot, e in sorted(self.robot_scale.items())]
        zones = [{"id": area, "name": area, "x": NODES[area][0], "z": NODES[area][1], "r": AREA_RADIUS,
                  "speed_scale": e["speed_scale"], "reason": e["reason"]} for area, e in sorted(self.area_scale.items())]
        proposals = [{"id": p["id"], "robot": p["robot"], "zone": p["zone"], "speed_scale": p["speed_scale"], "reason": p["reason"]}
                     for p in self.proposals.values()]
        return robots, zones, proposals

    def take_events(self) -> list[str]:
        events, self.events = self.events, []
        return events
