"""ロボもりのルール AI。外部の AI や API は使わず、rules.json の数字（しきい値・時間の幅・回数）だけで判断する。

- 数える材料: Unity から届いたロボットのいまの様子（Live.robots）と、中核が記録した出来事
  （ロボットが止まった・動き出した・段階が変わった、センサー中継の段階・KEY2・途切れ）、M5 の揺れの最大。
- 判断: ルールごとに「安全 / 注意 / 危険」を決め、数字の根拠を付ける。注意が重なったら危険にする（combine）。
- 答え: 質問の言葉（ロボットの名前、「危ない」「止まった」「どうすれば」「基準」など）から、決まった形の答えを作る。
  同じ様子と同じ質問なら、いつも同じ答えになる。ネットにも出ない。

Django には触らない（材料は呼ぶ側が渡す）ので、そのままテストできる。
"""
from __future__ import annotations

import hashlib
import json
import re
import unicodedata
from datetime import datetime, timedelta
from pathlib import Path

RULES_FILE = Path(__file__).with_name("rules.json")
ORDER = {"safe": 0, "caution": 1, "danger": 2}
ROBOT_METRICS = {"stops", "degraded", "stopped_seconds", "limited_now", "m5_raised", "m5_key2", "m5_gaps", "m5_peak_mg"}
STORE_METRICS = {"robots_stopped", "store_stops", "robots_in_danger"}
CONDITION_WORDS = {"Normal": "正常", "Warning": "注意", "Limited": "制限"}


class RulesError(ValueError):
    pass


# ---- 基準（rules.json） ----------------------------------------------------------------------------

def _number(value, where: str, allow_none: bool = True):
    if value is None and allow_none:
        return None
    if isinstance(value, bool) or not isinstance(value, (int, float)) or value < 0:
        raise RulesError(f"{where} は 0 以上の数か null")
    return value


def validate(data: dict) -> dict:
    """形を確かめる。おかしければ RulesError（どこがおかしいかを言う）。"""
    if not isinstance(data, dict):
        raise RulesError("全体がオブジェクトではない")
    for group, metrics in (("robot_rules", ROBOT_METRICS), ("store_rules", STORE_METRICS)):
        rules = data.get(group)
        if not isinstance(rules, list) or not rules:
            raise RulesError(f"{group} がない")
        for i, rule in enumerate(rules):
            where = f"{group}[{i}]"
            if not isinstance(rule, dict) or not isinstance(rule.get("id"), str) or not isinstance(rule.get("label"), str):
                raise RulesError(f"{where} に id と label がない")
            if rule.get("metric") not in metrics:
                raise RulesError(f"{where}.metric は {sorted(metrics)} のどれか")
            caution = _number(rule.get("caution"), f"{where}.caution")
            danger = _number(rule.get("danger"), f"{where}.danger")
            _number(rule.get("window_minutes"), f"{where}.window_minutes")
            if caution is None and danger is None:
                raise RulesError(f"{where} は caution か danger のどちらかが要る")
            if caution is not None and danger is not None and danger < caution:
                raise RulesError(f"{where} は danger を caution 以上にする")
            if not isinstance(rule.get("advice", {}), dict):
                raise RulesError(f"{where}.advice はオブジェクト")
    combine = data.get("combine", {})
    if not isinstance(combine, dict):
        raise RulesError("combine はオブジェクト")
    _number(combine.get("cautions_make_danger"), "combine.cautions_make_danger")
    return data


_cache: dict = {}


def load_rules(path: Path = RULES_FILE) -> tuple[dict, str]:
    """基準を読む。書き換えたら次から使う。読めない・形がおかしいときは、前に読めた基準を使い、問題を返す。"""
    try:
        stamp = path.stat().st_mtime_ns
        if _cache.get("path") == str(path) and _cache.get("stamp") == stamp:
            return _cache["rules"], ""
        rules = validate(json.loads(path.read_text(encoding="utf-8")))
        _cache.update(path=str(path), stamp=stamp, rules=rules)
        return rules, ""
    except (OSError, ValueError) as error:
        if _cache.get("rules"):
            return _cache["rules"], f"rules.json を使えないので、前に読めた基準で判断している（{error}）"
        raise


# ---- 数える ---------------------------------------------------------------------------------------

def _within(events: list[dict], now: datetime, minutes) -> list[dict]:
    if minutes is None:
        return events
    since = now - timedelta(minutes=minutes)
    return [e for e in events if e["at"] >= since]


def robot_metric(metric: str, rid: str, robot: dict, events: list[dict], relay: dict, now: datetime, minutes) -> int | None:
    """1 台のロボットの値。測れないもの（M5 がつながっていないロボットの M5 の値など）は None。"""
    mine = [e for e in _within(events, now, minutes) if (e.get("data") or {}).get("robot") == rid]
    if metric == "stops":
        return sum(1 for e in mine if e["source"] == "robot" and e["kind"] == "stop")
    if metric == "degraded":
        return sum(1 for e in mine if e["source"] == "robot" and e["kind"] == "condition" and e["data"].get("to") in ("Warning", "Limited"))
    if metric == "stopped_seconds":
        if not robot.get("stopped"):
            return 0
        stops = [e["at"] for e in events if e["source"] == "robot" and e["kind"] == "stop" and (e.get("data") or {}).get("robot") == rid]
        return int((now - max(stops)).total_seconds()) if stops else 0
    if metric == "limited_now":
        return 1 if robot.get("condition") == "Limited" else 0
    if not metric.startswith("m5_") or relay.get("robot") != rid:
        return None
    sensor = [e for e in _within(events, now, minutes) if e["source"] == "relay"]
    if metric == "m5_raised":
        return sum(1 for e in sensor if (e["kind"] == "level" and (e.get("data") or {}).get("level") in ("Warning", "Limited")) or e["kind"] == "hold")
    if metric == "m5_key2":
        return sum(1 for e in sensor if e["kind"] == "key2" and (e.get("data") or {}).get("accepted"))
    if metric == "m5_gaps":
        return sum(1 for e in sensor if e["kind"] == "gap")
    if metric == "m5_peak_mg":
        since = int(now.timestamp() // 60) - int(minutes or 10)
        return max([p[1] for p in relay.get("peaks") or [] if isinstance(p, list) and len(p) == 2 and p[0] > since] or [0])
    return None


def _level(rule: dict, value) -> str:
    if value is None:
        return "safe"
    if rule.get("danger") is not None and value >= rule["danger"]:
        return "danger"
    if rule.get("caution") is not None and value >= rule["caution"]:
        return "caution"
    return "safe"


def _finding(rule: dict, value, level: str, scope: str) -> dict:
    return {"scope": scope, "rule": rule["id"], "label": rule["label"], "value": value, "unit": rule.get("unit", ""),
            "window_minutes": rule.get("window_minutes"), "caution": rule.get("caution"), "danger": rule.get("danger"),
            "level": level, "why": rule.get("why", ""), "advice": (rule.get("advice") or {}).get(level, "")}


def judge(rules: dict, robots: list[dict], events: list[dict], relay: dict, now: datetime) -> dict:
    """ロボットごとと店全体の判断。findings は注意以上だけ（安全なルールは根拠に出さない）。"""
    combine = (rules.get("combine") or {}).get("cautions_make_danger")
    result = {"robots": [], "store": None}
    for robot in robots:
        rid = str(robot.get("id") or "")
        if not rid:
            continue
        findings, values = [], {}
        for rule in rules["robot_rules"]:
            value = robot_metric(rule["metric"], rid, robot, events, relay, now, rule.get("window_minutes"))
            values[rule["id"]] = value
            level = _level(rule, value)
            if level != "safe":
                findings.append(_finding(rule, value, level, rid))
        level = max((f["level"] for f in findings), key=ORDER.get, default="safe")
        cautions = sum(1 for f in findings if f["level"] == "caution")
        combined = bool(combine) and level == "caution" and cautions >= combine
        if combined:
            level = "danger"
        result["robots"].append({"id": rid, "level": level, "findings": findings, "values": values, "combined": combined,
                                 "condition": robot.get("condition") or "Normal", "stopped": bool(robot.get("stopped")),
                                 "sensor": relay.get("robot") == rid})
    in_danger = sum(1 for r in result["robots"] if r["level"] == "danger")
    store_values = {
        "robots_stopped": sum(1 for r in robots if r.get("stopped")),
        "store_stops": sum(1 for e in events if e["source"] == "robot" and e["kind"] == "stop"),
        "robots_in_danger": in_danger,
    }
    findings, values = [], {}
    for rule in rules["store_rules"]:
        value = store_values[rule["metric"]]
        if rule["metric"] == "store_stops" and rule.get("window_minutes") is not None:
            value = sum(1 for e in _within(events, now, rule["window_minutes"]) if e["source"] == "robot" and e["kind"] == "stop")
        values[rule["id"]] = value
        level = _level(rule, value)
        if level != "safe":
            findings.append(_finding(rule, value, level, "store"))
    level = max((f["level"] for f in findings), key=ORDER.get, default="safe")
    result["store"] = {"level": level, "findings": findings, "values": values}
    return result


# ---- 答える ---------------------------------------------------------------------------------------

def _words(rules: dict) -> dict:
    return {"safe": "安全", "caution": "注意", "danger": "危険", **(rules.get("levels") or {})}


def _evidence(finding: dict, words: dict) -> str:
    span = f"{finding['window_minutes']} 分で" if finding.get("window_minutes") else "いま"
    limit = finding["danger"] if finding["level"] == "danger" else finding["caution"]
    unit = finding.get("unit") or ""
    value = f"{finding['value']}{unit}" if unit else ""
    head = f"{finding['label']}: {span} {value}".rstrip() if value else f"{finding['label']}"
    return f"{head}（{words[finding['level']]}の基準 {limit}{unit} 以上）" if value else f"{head}（{words[finding['level']]}）"


def _robot_line(robot: dict, words: dict) -> str:
    state = "止まっている" if robot["stopped"] else CONDITION_WORDS.get(robot["condition"], robot["condition"])
    return f"ロボット {robot['id']}: {words[robot['level']]}（いま{state}）"


def _robot_answer(robot: dict, rules: dict, words: dict) -> str:
    lines = [_robot_line(robot, words)]
    if not robot["findings"]:
        lines.append("基準を超えたものはない。")
    for f in robot["findings"]:
        lines.append("・" + _evidence(f, words) + (f" — {f['why']}" if f.get("why") else ""))
    if robot["combined"]:
        lines.append("・" + ((rules.get("combine") or {}).get("why") or "注意が重なったので危険とみなした"))
    v = robot["values"]
    counts = [f"止まった {v.get('stops', 0)} 回", f"注意・制限 {v.get('degraded', 0)} 回"]
    if robot["sensor"]:
        counts += [f"M5 の実測で注意以上 {v.get('m5_raised') or 0} 回", f"KEY2 で戻した {v.get('m5_key2') or 0} 回",
                   f"揺れの最大 {v.get('m5_peak') or 0} mg"]
    lines.append("数えたもの: " + "、".join(counts))
    return "\n".join(lines)


def _advice(result: dict, words: dict) -> list[str]:
    tips = []
    for robot in sorted(result["robots"], key=lambda r: -ORDER[r["level"]]):
        for f in sorted(robot["findings"], key=lambda f: -ORDER[f["level"]]):
            if f.get("advice"):
                tips.append(f"ロボット {robot['id']}（{words[f['level']]}・{f['label']}）: {f['advice']}")
    for f in result["store"]["findings"]:
        if f.get("advice"):
            tips.append(f"店全体（{words[f['level']]}・{f['label']}）: {f['advice']}")
    return tips


INTENTS = [
    ("rules", ("基準", "ルール", "しきい値", "閾値", "決まり", "パラメータ")),
    ("advice", ("どうすれば", "どうしたら", "対処", "何をすれば", "なにをすれば", "対応", "おすすめ", "すべき")),
    ("why", ("なぜ", "なんで", "理由", "根拠")),
    ("stops", ("止まった", "停止", "回数", "何回", "なんかい")),
    ("sensor", ("m5", "センサ", "揺れ", "振動", "stick", "key2")),
    ("status", ("危", "安全", "大丈夫", "状況", "様子", "どう", "全体", "店")),
]


def intent_of(question: str) -> str:
    text = unicodedata.normalize("NFKC", question or "").lower()
    for name, words in INTENTS:
        if any(w in text for w in words):
            return name
    return "help"


def robot_of(question: str, ids: list[str]) -> str | None:
    text = unicodedata.normalize("NFKC", question or "")
    for rid in ids:
        if re.search(rf"(?:ロボット|robot)\s*{re.escape(rid)}(?![A-Za-z0-9])", text, re.IGNORECASE) \
                or re.search(rf"(?<![A-Za-z0-9]){re.escape(rid)}(?![A-Za-z0-9])", text):
            return rid
    return None


def answer(question: str, rules: dict, result: dict) -> dict:
    """質問への答え。intent は何を聞かれたと受け取ったか、level は答えの重さ（画面の色）。"""
    words = _words(rules)
    robots = {r["id"]: r for r in result["robots"]}
    rid = robot_of(question, list(robots))
    intent = intent_of(question)
    store = result["store"]
    if not robots and intent not in ("rules", "help"):
        return {"intent": intent, "level": "safe", "robot": None,
                "answer": "Unity（または代役）から、まだロボットの様子が届いていない。Unity を再生してから、もう一度聞いてください。"}
    if rid and intent in ("status", "why", "stops", "help", "sensor"):
        robot = robots[rid]
        if intent == "sensor" and not robot["sensor"]:
            text = f"ロボット {rid} には M5 がつながっていない（M5 で測っているのは別のロボット）。\n" + _robot_answer(robot, rules, words)
        else:
            text = _robot_answer(robot, rules, words)
        return {"intent": intent, "level": robot["level"], "robot": rid, "answer": text}
    if intent == "advice":
        tips = [t for t in _advice(result, words) if not rid or t.startswith(f"ロボット {rid}")]
        text = "\n".join(["いまの判断からのおすすめ（重いもの順）:"] + ["・" + t for t in tips[:6]]) if tips else \
            "基準を超えたものはないので、いまはこのままでよい。"
        level = robots[rid]["level"] if rid else max([store["level"]] + [r["level"] for r in robots.values()], key=ORDER.get)
        return {"intent": intent, "level": level, "robot": rid, "answer": text}
    if intent == "stops":
        window = next((r.get("window_minutes") for r in rules["robot_rules"] if r["metric"] == "stops"), 30)
        lines = [f"止まった回数（{window} 分）:"] + [f"・ロボット {r['id']}: {r['values'].get('stops', 0)} 回"
                                                + ("（いま止まっている）" if r["stopped"] else "") for r in robots.values()]
        lines.append(f"店全体: {store['values'].get('store_stops', 0)} 回、いま止まっているのは {store['values'].get('robots_stopped', 0)} 台")
        level = max([r["level"] for r in robots.values()] + [store["level"]], key=ORDER.get)
        return {"intent": intent, "level": level, "robot": None, "answer": "\n".join(lines)}
    if intent == "sensor":
        sensed = [r for r in robots.values() if r["sensor"]]
        if not sensed:
            return {"intent": intent, "level": "safe", "robot": None,
                    "answer": "M5（センサー中継）の記録が届いていない。中継を起動すると、実測で判断に入る。"}
        return {"intent": intent, "level": sensed[0]["level"], "robot": sensed[0]["id"], "answer": _robot_answer(sensed[0], rules, words)}
    if intent == "rules":
        lines = ["判断の基準（rules.json。その値以上で注意・危険）:"]
        for rule in rules["robot_rules"] + rules["store_rules"]:
            span = f"{rule['window_minutes']} 分で" if rule.get("window_minutes") else "いま"
            parts = [f"注意 {rule['caution']}{rule.get('unit', '')}" if rule.get("caution") is not None else "",
                     f"危険 {rule['danger']}{rule.get('unit', '')}" if rule.get("danger") is not None else ""]
            lines.append(f"・{rule['label']}（{span}）: " + "、".join(p for p in parts if p))
        combine = (rules.get("combine") or {}).get("cautions_make_danger")
        if combine:
            lines.append(f"・1 台で注意が {combine} つ以上重なったら危険")
        return {"intent": intent, "level": "safe", "robot": None, "answer": "\n".join(lines)}
    if intent in ("status", "why"):
        lines = [f"店全体: {words[store['level']]}"]
        for f in store["findings"]:
            lines.append("・" + _evidence(f, words))
        for robot in sorted(robots.values(), key=lambda r: (-ORDER[r["level"]], r["id"])):
            lines.append(_robot_line(robot, words))
            for f in robot["findings"][:3]:
                lines.append("　・" + _evidence(f, words))
        lines.append("くわしくは「B は？」、対処は「どうすればいい？」と聞いてください。")
        level = max([store["level"]] + [r["level"] for r in robots.values()], key=ORDER.get)
        return {"intent": intent, "level": level, "robot": None, "answer": "\n".join(lines)}
    return {"intent": "help", "level": "safe", "robot": None,
            "answer": "聞けること: 「いま危ない？」「B は？」「止まった回数は？」「M5 の揺れは？」「どうすればいい？」「判断の基準は？」。"
                      "答えは rules.json の数字だけで決めている（外部の AI は使わない）。"}


# ---- 外へ出す要約（扉 2 の共有と、AT プロトコルの記録） ----------------------------------------------------

def rules_version(rules: dict) -> str:
    """基準の版。中身が同じなら同じ値になる（記録が、どの基準で判断したかを名乗る）。"""
    return hashlib.sha256(json.dumps(rules, ensure_ascii=False, sort_keys=True).encode("utf-8")).hexdigest()[:12]


def share_summary(rules: dict, result: dict, relay: dict) -> dict:
    """外へ出してよい数字だけ（座標・人の名前・生の測定値の並びは入れない）。数はすべて整数。"""
    window = next((r.get("window_minutes") for r in rules["robot_rules"] if r["metric"] == "stops"), 30)
    robots = [{"robot": r["id"], "level": r["level"], "stops": int(r["values"].get("stops") or 0),
               "degraded": int(r["values"].get("degraded") or 0)} for r in result["robots"]]
    sensor = None
    sensed = next((r for r in result["robots"] if r["sensor"]), None)
    if sensed:
        v = sensed["values"]
        sensor = {"device": "M5StickS3", "robot": sensed["id"], "thresholdMg": int(relay.get("threshold_mg") or 0),
                  "peakMg": int(v.get("m5_peak") or 0), "raised": int(v.get("m5_raised") or 0),
                  "humanRecoveries": int(v.get("m5_key2") or 0), "gaps": int(v.get("m5_gaps") or 0)}
    return {"rulesVersion": rules_version(rules), "windowMinutes": int(window or 0), "store": result["store"]["level"],
            "robots": robots, "sensor": sensor}
