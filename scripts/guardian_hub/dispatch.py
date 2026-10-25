"""ロボットが故障したとき、誰が対応すべきかを推薦する。

店内を点（ノード）と通り道（辺）のグラフにし、状況に合う役割の人それぞれから、ロボットのいる場所までの
最短経路（ダイクストラ法）で着くまでの時間を出し、早い順に 1 位と次点を推薦する。
監視 AI が「床の段差かもしれない」とした場所は、歩くのにも時間がかかるものとして重みを上げる。
推薦であって命令ではない。決めるのは人。
"""
from __future__ import annotations

import heapq
import math
from dataclasses import dataclass

# 店内の点と通り道は、Unity のシミュレーターの配置を写した layout.py から取る
from layout import EDGES, KITCHEN_STAFF, NODES  # noqa: E402

WALK_SPEED = 1.2      # 人が店内を歩く速さ（m/s）
BUSY_PENALTY = 30.0   # 手がふさがっている人は、これだけ遅れて着くものとする（秒）
HAZARD_FACTOR = 1.5   # 段差などがあるとした場所を通る辺は、これだけ時間がかかるものとする

# 状況ごとに、来てほしい役割（先にあるほどふさわしい）
SITUATIONS = {
    "stopped": ("停止（保持）", ("保守担当", "管理者")),
    "limited": ("揺れが大きい（制限）", ("保守担当", "フロア店員")),
    "stuck": ("荷物を載せたまま動けない", ("フロア店員", "保守担当")),
}
# Unity にいない人の居場所（前提）。保守担当は店の外から入口を通って来る。調理担当は Unity の厨房の人の位置
ROSTER = [
    {"id": "maintenance", "name": "保守担当", "role": "保守担当", "at": NODES["入口"]},
    {"id": "manager", "name": "管理者", "role": "管理者", "at": NODES["セルフレジ"]},
    {"id": "kitchen", "name": "調理担当", "role": "調理担当", "at": KITCHEN_STAFF},
]


def nearest_node(x: float, z: float) -> str:
    return min(NODES, key=lambda name: math.dist(NODES[name], (x, z)))


def _graph(hazards: set[str]) -> dict[str, list[tuple[str, float]]]:
    graph: dict[str, list[tuple[str, float]]] = {name: [] for name in NODES}
    for a, b in EDGES:
        seconds = math.dist(NODES[a], NODES[b]) / WALK_SPEED
        if a in hazards or b in hazards:
            seconds *= HAZARD_FACTOR
        graph[a].append((b, seconds))
        graph[b].append((a, seconds))
    return graph


def shortest_path(start: str, goal: str, hazards: set[str] = frozenset()) -> tuple[float, list[str]]:
    """ダイクストラ法。着くまでの秒数と、通る点の並びを返す。つながっていなければ (inf, [])。"""
    graph = _graph(set(hazards))
    best = {start: 0.0}
    previous: dict[str, str] = {}
    queue = [(0.0, start)]
    while queue:
        seconds, node = heapq.heappop(queue)
        if node == goal:
            path = [goal]
            while path[-1] != start:
                path.append(previous[path[-1]])
            return seconds, path[::-1]
        if seconds > best.get(node, math.inf):
            continue
        for neighbour, cost in graph[node]:
            candidate = seconds + cost
            if candidate < best.get(neighbour, math.inf):
                best[neighbour] = candidate
                previous[neighbour] = node
                heapq.heappush(queue, (candidate, neighbour))
    return math.inf, []


@dataclass
class Failure:
    robot: str
    kind: str      # stopped / limited / stuck
    x: float
    z: float
    since: float


def classify(robot: dict, stuck_seconds: float) -> str | None:
    """ロボット 1 台の telemetry から、人を呼ぶべき故障かを決める。注意（Warning）は仕事を続けられるので呼ばない。"""
    if robot["stopped"]:
        return "stopped"
    if robot["condition"] == "Limited":
        return "limited"
    if robot["carrying"] and stuck_seconds >= 30:
        return "stuck"
    return None


def candidates(people: list[dict]) -> list[dict]:
    """推薦の候補: Unity にいる店員（位置と手のふさがり）と、ふだんの居場所に置いた人。"""
    found = [dict(entry, x=entry["at"][0], z=entry["at"][1], busy=False) for entry in ROSTER]
    for person in people:
        if person.get("role") == "floor":
            found.append({"id": person["id"], "name": "フロア店員", "role": "フロア店員",
                          "x": person["x"], "z": person["z"], "busy": person.get("busy", False)})
    return found


def recommend(failure: Failure, people: list[dict], hazards: set[str] = frozenset()) -> dict | None:
    """1 台の故障に、1 位と次点を推薦する。役割の合う人がいなければ None。"""
    title, roles = SITUATIONS[failure.kind]
    goal = nearest_node(failure.x, failure.z)
    ranked = []
    for person in candidates(people):
        if person["role"] not in roles:
            continue
        start = nearest_node(person["x"], person["z"])
        seconds, route = shortest_path(start, goal, hazards)
        if not route:
            continue
        seconds += math.dist((person["x"], person["z"]), NODES[start]) / WALK_SPEED
        if person["busy"]:
            seconds += BUSY_PENALTY
        # 役割の順（ふさわしさ）を第一に、着く早さを第二にする
        ranked.append((roles.index(person["role"]), seconds, person, route))
    if not ranked:
        return None
    ranked.sort(key=lambda item: (item[0], item[1]))
    _, seconds, person, route = ranked[0]
    backup = ranked[1][2]["name"] if len(ranked) > 1 else ""
    return {"robot": failure.robot, "situation": title, "role": person["role"], "person": person["name"],
            "eta_s": round(seconds, 1), "route": route,
            # Unity の JsonUtility は入れ子の配列（[[x, z], …]）を読めないので、点は {x, z} の並びにする
            "points": [{"x": NODES[n][0], "z": NODES[n][1]} for n in route],
            "from": {"x": round(person["x"], 2), "z": round(person["z"], 2)}, "backup": backup, "busy": person["busy"]}
