"""Unity と監視ハブのあいだの JSON Lines（TCP 127.0.0.1:47612）。

Unity → ハブ  telemetry（0.5 秒ごと）
  {"v":1,"type":"telemetry","boot":"unity-…","seq":12,"t":34.5,"door":"store",
   "robots":[{"id":"B","x":-4.2,"z":0.0,"speed":0.8,"condition":"Warning","stopped":false,"held":false,
              "task":"Food","phase":"ToDropoff","carrying":true,"vibration":0.5,"speed_scale":1.0,"wait":""}],
   "people":[{"id":"staff","role":"floor","x":1.6,"z":-4.3,"busy":false}],
   "approved":["p-3"],"declined":[],"restored":["zone:通路中"]}
  ロボットの says は「今の状況は？」へのロボット自身の答え（RobomoriTalk）、table は仕事のテーブル（1-3、なしは 0）。
  people の guest は店内のお客さん（state: 来店・席待ち・ご案内・注文待ち・食事中・…）。seats は椅子ごとの空き具合、
  talk は人がロボットにした最後の質問と答え（n は通し番号。0 はまだなし）。
Unity → ハブ  scene（つないだとき・店内が変わったとき）
  {"v":1,"type":"scene","boot":"unity-…","seq":1,"west":-9.74,"east":9.74,"south":-6.04,"north":6.04,
   "things":[{"id":"Dining Table 1","kind":"table","label":"テーブル1","x":6.1,"z":-3.4,"w":1.9,"d":1.3}],
   "spots":[{"id":"home-A","kind":"home","label":"A 待機","x":-4.2,"z":-5.25}]}
  Unity のシーンにある物そのものから読んだ配置（中核に写しを持たない）。カメラの検出のように、名前と床の上の枠で表す。
ハブ → Unity  advice（0.5 秒ごと）: build_advice() を見る。

Unity の JsonUtility が出す値をそのまま受ける。壊れた行や範囲外の値でハブが止まらないように、
ここで型と範囲を確かめ、知らない項目は読み飛ばし、使えない値は「なし」として扱う。
"""
from __future__ import annotations

import json
import math

VERSION = 1
MAX_LINE = 16384
CONDITIONS = ("Normal", "Warning", "Limited")
DOORS = ("store", "standalone")
SEAT_STATES = ("occupied", "clearing", "ready", "reserved")
MAX_PEOPLE = 32
MAX_THINGS = 200
MAX_SPOTS = 64


class Rejected(ValueError):
    """この行は使えない（理由つき）。数えて捨てるだけで、ハブは止めない。"""


def _no_constants(name: str):
    raise ValueError(f"非数値 {name}")


def decode(raw: bytes | str) -> dict:
    """1 行を辞書にする。NaN / Infinity、BOM、CRLF、長すぎる行、JSON でない行は Rejected。"""
    if isinstance(raw, bytes):
        if len(raw) > MAX_LINE:
            raise Rejected("行が長すぎる")
        raw = raw.decode("utf-8", errors="replace")
    text = raw.lstrip("﻿").strip()
    if len(text) > MAX_LINE:
        raise Rejected("行が長すぎる")
    if not text.startswith("{"):
        raise Rejected("JSON のオブジェクトではない")
    try:
        message = json.loads(text, parse_constant=_no_constants)
    except ValueError as error:
        raise Rejected(f"JSON として読めない（{error}）") from None
    if not isinstance(message, dict):
        raise Rejected("JSON のオブジェクトではない")
    return message


def _number(value, low: float | None = None, high: float | None = None) -> float | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return None
    if (low is not None and value < low) or (high is not None and value > high):
        return None
    return float(value)


def _text(value, limit: int = 64) -> str:
    return value[:limit] if isinstance(value, str) else ""


def _flag(value) -> bool:
    return value is True


def parse_telemetry(message: dict) -> dict:
    """telemetry を、ハブが使う形に整える。ロボットの id と位置が読めない行だけは Rejected にする。"""
    if message.get("v") != VERSION:
        raise Rejected(f"版が違う（v={message.get('v')!r}）")
    if message.get("type") != "telemetry":
        raise Rejected(f"telemetry ではない（type={message.get('type')!r}）")
    seq = message.get("seq")
    if isinstance(seq, bool) or not isinstance(seq, int) or seq < 0:
        raise Rejected("seq がない")
    robots = []
    for item in message.get("robots") or []:
        if not isinstance(item, dict):
            continue
        robot_id = _text(item.get("id"), 8)
        x, z = _number(item.get("x"), -100, 100), _number(item.get("z"), -100, 100)
        if not robot_id or x is None or z is None:
            continue  # 位置がまだ決まっていない（生成直後の NaN など）ロボットは、この回だけ飛ばす
        condition = item.get("condition") if item.get("condition") in CONDITIONS else "Normal"
        robots.append({
            "id": robot_id, "x": x, "z": z,
            "speed": _number(item.get("speed"), 0, 20) or 0.0,
            "condition": condition,
            "stopped": _flag(item.get("stopped")), "held": _flag(item.get("held")),
            "task": _text(item.get("task"), 16), "phase": _text(item.get("phase"), 24),
            "carrying": _flag(item.get("carrying")),
            "vibration": _number(item.get("vibration"), 0, 1),
            "speed_scale": _number(item.get("speed_scale"), 0, 1) or 1.0,
            "wait": _text(item.get("wait"), 120),
            "says": _text(item.get("says"), 160),
            "table": int(_number(item.get("table"), 0, 99) or 0),
        })
    if not robots:
        raise Rejected("使えるロボットの行がない")
    people = []
    for item in message.get("people") or []:
        if not isinstance(item, dict) or len(people) >= MAX_PEOPLE:
            continue
        x, z = _number(item.get("x"), -100, 100), _number(item.get("z"), -100, 100)
        if x is None or z is None:
            continue
        people.append({"id": _text(item.get("id"), 32) or "person", "role": _text(item.get("role"), 16),
                       "state": _text(item.get("state"), 16), "x": x, "z": z, "busy": _flag(item.get("busy"))})
    seats = []
    for item in message.get("seats") or []:
        if isinstance(item, dict) and item.get("state") in SEAT_STATES and len(seats) < MAX_PEOPLE:
            seats.append({"id": _text(item.get("id"), 32), "state": item["state"], "table": int(_number(item.get("table"), 0, 99) or 0)})
    talk = None
    said = message.get("talk")
    if isinstance(said, dict) and isinstance(said.get("n"), int) and not isinstance(said.get("n"), bool) and said["n"] > 0:
        talk = {"n": said["n"], "robot": _text(said.get("robot"), 8), "question": _text(said.get("question"), 120),
                "answer": _text(said.get("answer"), 300), "by": _text(said.get("by"), 32)}
    return {
        "boot": _text(message.get("boot"), 64), "seq": seq, "t": _number(message.get("t")) or 0.0,
        "door": message.get("door") if message.get("door") in DOORS else "store",
        "robots": robots, "people": people, "seats": seats, "talk": talk,
        "approved": [_text(i) for i in message.get("approved") or [] if isinstance(i, str)],
        "declined": [_text(i) for i in message.get("declined") or [] if isinstance(i, str)],
        # 提案がなくても、人が Unity で戻した減速（"robot:B" / "zone:通路中"）
        "restored": [_text(i) for i in message.get("restored") or [] if isinstance(i, str) and i.startswith(("robot:", "zone:"))],
    }


def parse_scene(message: dict) -> dict:
    """scene（Unity のシーンから読んだ店内の配置）を整える。部屋の大きさが読めない行は Rejected。
    物と点は、読めるものだけを残す（位置と大きさは部屋のまわり 100 m 以内、大きさは 1 cm〜30 m）。"""
    if message.get("v") != VERSION:
        raise Rejected(f"版が違う（v={message.get('v')!r}）")
    if message.get("type") != "scene":
        raise Rejected(f"scene ではない（type={message.get('type')!r}）")
    room = {side: _number(message.get(side), -100, 100) for side in ("west", "east", "south", "north")}
    if None in room.values() or room["west"] >= room["east"] or room["south"] >= room["north"]:
        raise Rejected("部屋の大きさが読めない")
    things = []
    for item in message.get("things") or []:
        if not isinstance(item, dict) or len(things) >= MAX_THINGS:
            continue
        x, z = _number(item.get("x"), -100, 100), _number(item.get("z"), -100, 100)
        w, d = _number(item.get("w"), 0.01, 30), _number(item.get("d"), 0.01, 30)
        kind = _text(item.get("kind"), 16)
        if None in (x, z, w, d) or not kind:
            continue
        things.append({"id": _text(item.get("id"), 48), "kind": kind, "label": _text(item.get("label"), 24),
                       "x": round(x, 3), "z": round(z, 3), "w": round(w, 3), "d": round(d, 3)})
    spots = []
    for item in message.get("spots") or []:
        if not isinstance(item, dict) or len(spots) >= MAX_SPOTS:
            continue
        x, z = _number(item.get("x"), -100, 100), _number(item.get("z"), -100, 100)
        kind = _text(item.get("kind"), 16)
        if x is None or z is None or not kind:
            continue
        spots.append({"id": _text(item.get("id"), 48), "kind": kind, "label": _text(item.get("label"), 24),
                      "x": round(x, 3), "z": round(z, 3)})
    if not things:
        raise Rejected("店内の物が 1 つも読めない")
    seq = message.get("seq")
    return {"boot": _text(message.get("boot"), 64), "seq": seq if isinstance(seq, int) and not isinstance(seq, bool) else 0,
            "room": room, "things": things, "spots": spots}


def encode(message: dict) -> bytes:
    """1 行にして送る形にする。OS の既定の文字コードに頼らず、いつも UTF-8 にする。
    日本語はエスケープせずにそのまま送る（sensor_bridge から Unity へ送っているのと同じ形で、JsonUtility で読めている）。"""
    return (json.dumps(message, ensure_ascii=False, allow_nan=False, separators=(",", ":")) + "\n").encode("utf-8")


def build_advice(boot: str, seq: int, gate: str, learning: bool, robots: list[dict], zones: list[dict],
                 proposals: list[dict], dispatch: list[dict]) -> dict:
    """ハブ → Unity。Unity の JsonUtility が読めるよう、配列は必ず置き、値は有限の数にする。"""
    return {"v": VERSION, "type": "advice", "boot": boot, "seq": seq, "gate": gate,
            "ai": "learning" if learning else "ok",
            "robots": robots, "zones": zones, "proposals": proposals, "dispatch": dispatch}
