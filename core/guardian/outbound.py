"""外へ出す材料と、Google Drive への書き出し（door2 コンテナと、時間割の書き出しで使う）。

書き出し先（/export）は PC 側のフォルダで、Google Drive に送るのは PC 側（Drive for desktop、または
core/host/psoc6_key.py の --drive-folder）。コンテナは Google の鍵を持たない。
"""
from __future__ import annotations

import json
import os
import re
from datetime import timedelta
from pathlib import Path

from django.utils import timezone

from door2 import compose_post, notable, render_report, stamp_now
from hub import write_replacing

from . import rulebot
from .models import Event, Gate, Live, Opinion, RelayTrust

# 外へ出す記録には、IP アドレスとポート（コンテナの住所など）を入れない
ADDRESS = re.compile(r"（?\b\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?\b）?")


def without_addresses(text: str) -> str:
    return ADDRESS.sub("", text)


def export_dir() -> Path:
    return Path(os.environ.get("EXPORT_DIR", "/export"))


def tags() -> list[str]:
    return [t.strip().lstrip("#") for t in os.environ.get("BLUESKY_HASHTAGS", "ロボもり,JPHACKS").split(",") if t.strip()]


def rule_judgement() -> tuple[dict, dict, str, dict]:
    """ルール AI の材料（Unity のいまの様子、記録した出来事、M5 の揺れ）を集めて判断する。外には出ない。"""
    rules, problem = rulebot.load_rules()
    now = timezone.now()
    live, trust = Live.get(), RelayTrust.get()
    events = [{"at": e.at, "source": e.source, "kind": e.kind, "data": e.data or {}}
              for e in Event.objects.filter(source__in=("robot", "relay"), at__gte=now - timedelta(hours=12))[:5000]]
    relay = {"robot": trust.robot if trust.file else "", "peaks": trust.peaks or [], "threshold_mg": trust.threshold_mg}
    robots = live.robots if live.unity_connected else []
    return rules, rulebot.judge(rules, robots, events, relay, now), problem, relay


def build_snapshot(gate: Gate) -> dict:
    """外へ出す材料。座標や生の測定値は入れない。前に扉 2 を閉じてからの記録だけを使う。"""
    live = Live.get()
    since = gate.last_closed_at or timezone.now() - timedelta(hours=12)
    records = [{"at": timezone.localtime(e.at).isoformat(timespec="seconds"), "kind": e.kind, "text": without_addresses(e.text)}
               for e in Event.objects.filter(at__gt=since, source__in=("ai", "dispatch", "door", "drill", "relay", "schedule")).order_by("at")[:200]]
    return {
        "slowdowns": [{k: s.get(k) for k in ("kind", "name", "scale", "reason")} for s in live.slowdowns],
        "robots": [{"id": r.get("id"), "condition": r.get("condition") or "Normal", "stopped": bool(r.get("stopped"))} for r in live.robots],
        "dispatch": [{k: d.get(k) for k in ("robot", "situation", "person", "eta_s", "backup")} for d in live.dispatch],
        "records": records,
        "judgement": _judgement_for_sharing(),
    }


def _judgement_for_sharing() -> dict | None:
    """ルール AI の判断の要約。基準が読めないときは出さない（共有は止めない）。"""
    try:
        rules, result, _, relay = rule_judgement()
    except (OSError, ValueError):
        return None
    return rulebot.share_summary(rules, result, relay)


def carried_opinions(limit: int = 20) -> list[dict]:
    """受け取ったが、まだ店の記録に載せていない意見（返信した人の DID と、その返信への strongRef）。"""
    found = []
    for reply in Opinion.objects.filter(source="reply")[:100]:
        detail = reply.detail or {}
        if not detail.get("in_record") and detail.get("did") and detail.get("cid") and reply.uri:
            found.append({"author": detail["did"], "post": {"uri": reply.uri, "cid": detail["cid"]}, "id": reply.pk})
    return found[:limit][::-1]


def export(directory: Path, snapshot: dict, result: dict) -> Path:
    """書き終えてから置き換えるので、書きかけが Drive に送られることはない。"""
    stamp = stamp_now()
    lines = [json.dumps(item, ensure_ascii=False) for item in snapshot["records"]]
    lines.append(json.dumps({"kind": "door2", "scheduled": bool(result.get("scheduled")), "text": result.get("text", ""),
                             "judgement": result.get("judgement"), "post": result.get("post"), "record": result.get("record"),
                             "replies": result.get("replies", []), "errors": result.get("errors", [])}, ensure_ascii=False))
    write_replacing(directory / f"guardian-records-{stamp}.jsonl", "\n".join(lines) + "\n")
    path = directory / f"guardian-records-{stamp}.md"
    write_replacing(path, render_report(stamp, snapshot["records"], result))
    return path


def scheduled_export(gate: Gate) -> tuple[Path | None, str]:
    """時間割の書き出し: Drive にだけ書く。インターネットには出ない（hub のコンテナで動く）。"""
    snapshot = build_snapshot(gate)
    result = {"scheduled": True, "text": compose_post(snapshot, tags()) if notable(snapshot) else "", "judgement": None,
              "post": None, "record": None, "replies": [], "errors": [], "notable": notable(snapshot), "gemini": "off", "bluesky": "off"}
    directory = export_dir()
    if not directory.is_dir():
        return None, f"書き出し先 {directory} がない"
    try:
        return export(directory, snapshot, result), ""
    except OSError as error:
        return None, f"書き出せなかった（{error}）"
