"""店内の配置。Unity のシミュレーターの値を写したもの（Unity の床の座標 x, z。メートル）。

推薦の経路（dispatch.py）、中核の画面の地図、Unity の代役（sim）は、すべてこの値を使う。
Unity 側の値を変えたら、ここも合わせる。出どころ:
  - 部屋・扉・セルフレジ・列:      Assets/Scripts/RestaurantEnclosure.cs（RestaurantEnclosure）
  - 厨房・返却台・テーブル・椅子・ソファ: Assets/Scripts/RestaurantVisuals.cs（Build）
  - ロボットの受け渡し場所:          Assets/Scripts/GuardianSimulator.cs（Kitchen・ReturnStation・Tables。受け渡しは台から 0.2 m）
  - ロボットの待機場所:              Assets/Scripts/GuardianSimulator.cs（home: x -4.2、A z -5.25 / B 0 / C 5.25）
  - 中央の通路（x = -1）:            Assets/Scripts/GuardianTrafficController.cs（単線通路の方式で通る線）
  - 店員の待機場所・客の通り道:      Assets/Scripts/GuardianDinerFlow.cs（StaffHome、SouthAisleZ、EastAisleX）
Unity のカメラは南から北を見下ろす（GuardianDemoInterface: Pivot (-1, 0.8, 0)、Yaw 0）。画面では北が上、東が右。
"""
from __future__ import annotations

ROOM = {"west": -9.74, "east": 9.74, "south": -6.04, "north": 6.04}

# 人が歩いて行き来できる点（推薦の経路のグラフの点）
NODES: dict[str, tuple[float, float]] = {
    "厨房": (-6.0, 3.6),        # 料理の受け取り口（Kitchen -6.2 から 0.2 m）
    "返却台": (-6.0, -3.6),      # 皿の返却口（ReturnStation -6.2 から 0.2 m）
    "待機場所": (-4.2, 0.0),     # ロボット B の待機場所（A と C は南北の端）
    "通路北": (-1.0, 3.6), "通路中": (-1.0, 0.0), "通路南": (-1.0, -3.6),
    "テーブル1": (3.6, -3.4), "テーブル2": (3.6, 0.0), "テーブル3": (3.6, 3.4),   # ロボットが止まる、テーブルの西側
    "入口": (1.5, -5.29),        # EntranceInside
    "セルフレジ": (4.6, -4.99),  # CheckoutSpot
}
EDGES = [
    ("厨房", "待機場所"), ("返却台", "待機場所"), ("厨房", "通路北"), ("返却台", "通路南"), ("待機場所", "通路中"),
    ("通路北", "通路中"), ("通路中", "通路南"),
    ("通路北", "テーブル3"), ("通路中", "テーブル2"), ("通路南", "テーブル1"),
    ("テーブル1", "テーブル2"), ("テーブル2", "テーブル3"),
    ("通路南", "入口"), ("入口", "セルフレジ"), ("セルフレジ", "テーブル1"),
]

ROBOT_HOMES = {"A": (-4.2, -5.25), "B": (-4.2, 0.0), "C": (-4.2, 5.25)}
STAFF_HOME = (1.6, -4.29)          # フロア店員（StaffHome: EntranceX + 0.1、South + 1.75）
KITCHEN_STAFF = (-7.55, 2.15)      # 調理担当（Kitchen Staff）

# 画面に描く家具（中心 x, z、幅 w（x 方向）, 奥行き d（z 方向）、名前）。大きさは見た目の目安
# label: "in" は箱の中に名前を書く。"out" は箱が小さいので、CALLOUTS の場所に書いて線で結ぶ
FIXTURES = [
    {"kind": "kitchen", "x": -8.75, "z": 2.7, "w": 1.2, "d": 1.4, "name": "コンロ", "label": "in"},
    {"kind": "kitchen", "x": -8.75, "z": 4.4, "w": 1.2, "d": 1.4, "name": "シンク", "label": "in"},
    {"kind": "kitchen", "x": -7.0, "z": 5.5, "w": 2.0, "d": 0.9, "name": "調理台", "label": "in"},
    {"kind": "counter", "x": -7.55, "z": 3.6, "w": 1.0, "d": 2.0, "name": "受け取り口", "label": "out"},
    {"kind": "counter", "x": -7.65, "z": -3.6, "w": 1.0, "d": 2.0, "name": "返却台", "label": "out"},
    {"kind": "booth", "x": 2.9, "z": 5.55, "w": 2.4, "d": 0.8, "name": "ソファ席", "label": "in"},
    {"kind": "booth", "x": 6.0, "z": 5.55, "w": 2.4, "d": 0.8, "name": "ソファ席", "label": "in"},
    {"kind": "kiosk", "x": 4.6, "z": -5.74, "w": 0.8, "d": 0.5, "name": "セルフレジ", "label": "out"},
    {"kind": "board", "x": 0.55, "z": -5.74, "w": 0.7, "d": 0.3, "name": "案内板", "label": "out"},
]
# 箱の外に書く名前（文字の書き出し x, z と、線を引く先 to）。anchor: start は右へ、end は左へ書く。
# ロボットが通る線（z = ±3.6 と x = -1）と待機場所に重ならない場所を選んである。家具の場所は動かさない
CALLOUTS = [
    {"text": "厨房", "x": -5.75, "z": 5.35, "anchor": "start", "size": "area"},
    {"text": "受け取り口", "x": -6.7, "z": 4.45, "anchor": "start", "to": {"x": -7.05, "z": 4.25}},
    {"text": "返却台", "x": -6.7, "z": -4.95, "anchor": "start", "to": {"x": -7.15, "z": -4.35}},
    {"text": "セルフレジ", "x": 5.3, "z": -5.62, "anchor": "start", "to": {"x": 5.0, "z": -5.74}},
    {"text": "案内板", "x": -0.05, "z": -5.62, "anchor": "end", "to": {"x": 0.2, "z": -5.74}},
]
TABLES = [{"name": f"テーブル{i + 1}", "x": 6.1, "z": z, "w": 1.9, "d": 1.3} for i, z in enumerate((-3.4, 0.0, 3.4))]
CHAIRS = [{"x": x, "z": z + dz} for z in (-3.4, 0.0, 3.4) for x in (4.8, 7.5) for dz in (-0.46, 0.46)]
DOORS = [{"name": "入口", "x": 1.5, "w": 1.2}, {"name": "出口", "x": 3.3, "w": 1.2}]   # 南の壁
CENTRAL_AISLE_X = -1.0
GUEST_AISLES = {"south_z": -4.65, "east_x": 8.75}


def as_dict() -> dict:
    """画面に渡す形（JSON にそのまま入る）。"""
    return {
        "room": ROOM, "nodes": {name: {"x": x, "z": z} for name, (x, z) in NODES.items()},
        "robot_homes": {rid: {"x": x, "z": z} for rid, (x, z) in ROBOT_HOMES.items()},
        "staff_home": {"x": STAFF_HOME[0], "z": STAFF_HOME[1]}, "kitchen_staff": {"x": KITCHEN_STAFF[0], "z": KITCHEN_STAFF[1]},
        "fixtures": FIXTURES, "callouts": CALLOUTS, "tables": TABLES, "chairs": CHAIRS, "doors": DOORS,
        "central_aisle_x": CENTRAL_AISLE_X, "guest_aisles": GUEST_AISLES,
    }


FIXTURE_KINDS = {"コンロ": "kitchen", "シンク": "kitchen", "調理台": "kitchen", "受け取り口": "pickup", "返却台": "return"}


def as_scene(boot: str = "copy") -> dict:
    """Unity の scene と同じ形（protocol.parse_scene が整えた後の形）。Unity の代役が送り、Unity がまだ何も
    送っていないときの画面にも使う。本物の配置は Unity のシーンから届く（GuardianHubLink の scene）。"""
    things = [{"id": f["name"], "kind": FIXTURE_KINDS.get(f["name"], f["kind"]), "label": f["name"],
               "x": f["x"], "z": f["z"], "w": f["w"], "d": f["d"]} for f in FIXTURES]
    things += [{"id": t["name"], "kind": "table", "label": t["name"], "x": t["x"], "z": t["z"], "w": t["w"], "d": t["d"]} for t in TABLES]
    things += [{"id": f"chair-{i}", "kind": "chair", "label": "", "x": c["x"], "z": c["z"], "w": 0.5, "d": 0.5} for i, c in enumerate(CHAIRS)]
    things += [{"id": d["name"], "kind": "door", "label": d["name"], "x": d["x"], "z": ROOM["south"], "w": d["w"], "d": 0.24} for d in DOORS]
    service = {"厨房": "kitchen", "返却台": "return", "テーブル1": "table1", "テーブル2": "table2", "テーブル3": "table3"}
    spots = [{"id": f"service-{key}", "kind": "service", "label": name, "x": NODES[name][0], "z": NODES[name][1]} for name, key in service.items()]
    spots += [{"id": f"home-{rid}", "kind": "home", "label": f"{rid} 待機", "x": x, "z": z} for rid, (x, z) in ROBOT_HOMES.items()]
    spots += [{"id": "staff", "kind": "staff", "label": "店員の待機場所", "x": STAFF_HOME[0], "z": STAFF_HOME[1]},
              {"id": "cook", "kind": "cook", "label": "調理担当", "x": KITCHEN_STAFF[0], "z": KITCHEN_STAFF[1]}]
    return {"boot": boot, "seq": 0, "room": dict(ROOM), "things": things, "spots": spots}


if __name__ == "__main__":   # PV（pv/src/unityLayout.json）を作り直す: python scripts/guardian_hub/layout.py > pv/src/unityLayout.json
    import json
    print(json.dumps(as_dict(), ensure_ascii=False, indent=1))
