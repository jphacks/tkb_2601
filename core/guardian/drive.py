"""Google Drive 行きの記録を、受信箱の形で見せるための読み取り（書き出しフォルダは読むだけ）。

- 書き出しフォルダ（/export、PC の Logs/drive_out）にある guardian-records-日時.md / .jsonl を、1 件ずつの記録にする。
- PC 側の鍵（psoc6_key.ps1 / .py）が Drive の同期フォルダへ写すたびに書く .drive-log.jsonl を読み、
  どれが Drive へ送れたか・うまくいかなかったかを合わせる。web はどちらにも書かない。
- 決まった名前の普通のファイルだけを読む（リンクは辿らない、大きすぎるものは読まない）。
"""
from __future__ import annotations

import json
import os
import re
from datetime import datetime
from pathlib import Path

from .outbound import export_dir

EXPORT_NAME = re.compile(r"^guardian-records-(\d{8}-\d{6})\.(md|jsonl)$")
STAMP = re.compile(r"^\d{8}-\d{6}$")
LOG_NAME = ".drive-log.jsonl"
MAX_ITEMS = 200
MAX_TEXT = 64 * 1024
MAX_JSONL = 512 * 1024
MAX_LOG = 256 * 1024
LOG_LINES = 300


def _regular(path: Path) -> os.stat_result | None:
    """リンクではない普通のファイルなら、その情報。"""
    try:
        info = path.lstat()
    except OSError:
        return None
    return info if not path.is_symlink() and os.path.isfile(path) else None


def _tail(path: Path, limit: int) -> str:
    info = _regular(path)
    if info is None:
        return ""
    with path.open("rb") as file:
        if info.st_size > limit:
            file.seek(info.st_size - limit)
            file.readline()   # 途中から読み始めた 1 行は捨てる
        return file.read().decode("utf-8", errors="replace")


def read_log(directory: Path) -> list[dict]:
    """PC 側の鍵が書いた写しの記録（新しい順）。読めない行は飛ばす。"""
    entries = []
    for line in _tail(directory / LOG_NAME, MAX_LOG).splitlines()[-LOG_LINES:]:
        try:
            item = json.loads(line)
        except ValueError:
            continue
        if not isinstance(item, dict):
            continue
        entries.append({
            "at": str(item.get("at", ""))[:32], "kind": str(item.get("kind", "copy"))[:16],
            "file": str(item.get("file", ""))[:80], "ok": item.get("ok") is True,
            "to": str(item.get("to", ""))[:200], "error": str(item.get("error", ""))[:200],
        })
    entries.reverse()
    return entries


def _when(stamp: str) -> str:
    try:
        return datetime.strptime(stamp, "%Y%m%d-%H%M%S").isoformat(timespec="seconds")
    except ValueError:
        return ""


def _summary(jsonl: Path) -> dict:
    """.jsonl の最後の行（扉 2 の結果）と記録の件数。"""
    text = _tail(jsonl, MAX_JSONL) if _regular(jsonl) else ""
    lines = [line for line in text.splitlines() if line.strip()]
    door2, records = {}, 0
    for line in lines:
        try:
            item = json.loads(line)
        except ValueError:
            continue
        if isinstance(item, dict) and item.get("kind") == "door2":
            door2 = item
        elif isinstance(item, dict):
            records += 1
    return {"door2": door2, "records": records}


def _title(door2: dict, records: int) -> tuple[str, str, str]:
    """受信箱に並べる件名、分類（scheduled / shared / dry / quiet / error）、ひとこと。"""
    errors = [str(e) for e in door2.get("errors") or []]
    post = door2.get("post") or {}
    text = str(door2.get("text") or "")
    if door2.get("scheduled"):
        return f"時間割の書き出し（記録 {records} 件）", "scheduled", text[:120] or "減速も故障もなかった"
    if errors:
        return "扉 2: うまくいかないことがあった", "error", errors[0][:120]
    if post.get("url"):
        return "扉 2: Bluesky に共有した", "shared", text[:120]
    if text:
        return "扉 2: 共有文を作った（試し運転）", "dry", text[:120]
    return f"扉 2: 共有するものはなかった（記録 {records} 件）", "quiet", "減速も故障もなかった"


def listing(directory: Path | None = None) -> dict:
    directory = directory or export_dir()
    log = read_log(directory) if directory.is_dir() else []
    sent: dict[str, dict] = {}
    failed: dict[str, dict] = {}
    for entry in reversed(log):   # 古い順にたどり、最後の結果を残す
        if entry["kind"] != "copy" or not entry["file"]:
            continue
        if entry["ok"]:
            sent[entry["file"]] = entry
            failed.pop(entry["file"], None)
        elif entry["file"] not in sent:
            failed[entry["file"]] = entry
    stamps: dict[str, dict] = {}
    if directory.is_dir():
        try:
            names = sorted(os.listdir(directory), reverse=True)
        except OSError:
            names = []
        for name in names:
            match = EXPORT_NAME.match(name)
            if not match:
                continue
            info = _regular(directory / name)
            if info is None:
                continue
            item = stamps.setdefault(match.group(1), {"id": match.group(1), "files": []})
            item["files"].append({"name": name, "size": info.st_size,
                                  "sent": sent.get(name), "failed": failed.get(name)})
    items = []
    for stamp in sorted(stamps, reverse=True)[:MAX_ITEMS]:
        item = stamps[stamp]
        summary = _summary(directory / f"guardian-records-{stamp}.jsonl")
        title, kind, preview = _title(summary["door2"], summary["records"])
        files = sorted(item["files"], key=lambda f: f["name"])
        sent_at = [f["sent"]["at"] for f in files if f["sent"]]
        item.update(at=_when(stamp), title=title, kind=kind, preview=preview, records=summary["records"], files=files,
                    synced="sent" if files and all(f["sent"] for f in files) else "failed" if any(f["failed"] for f in files) else "waiting",
                    sent_at=max(sent_at) if sent_at else "")
        items.append(item)
    last_found = next((e for e in log if e["kind"] == "drive"), None)
    target = next((e["to"] for e in log if e["kind"] == "copy" and e["ok"] and e["to"]), "") or (last_found or {}).get("to", "")
    return {
        "folder": "Logs/drive_out（この PC の書き出しフォルダ）",
        "drive": {"to": target, "found": bool(last_found["ok"]) if last_found else bool(target),
                  "checked_at": (last_found or {}).get("at", "")},
        "counts": {"all": len(items), "sent": sum(i["synced"] == "sent" for i in items),
                   "waiting": sum(i["synced"] == "waiting" for i in items), "failed": sum(i["synced"] == "failed" for i in items)},
        "items": items,
        "log": log[:60],
    }


def message(stamp: str, directory: Path | None = None) -> dict | None:
    """1 件の中身（Markdown の本文は文字として返す。画面は HTML として解釈しない）。"""
    if not STAMP.match(stamp or ""):
        return None
    directory = directory or export_dir()
    md = directory / f"guardian-records-{stamp}.md"
    jsonl = directory / f"guardian-records-{stamp}.jsonl"
    if _regular(md) is None and _regular(jsonl) is None:
        return None
    text = ""
    if _regular(md) is not None:
        with md.open("rb") as file:
            text = file.read(MAX_TEXT + 1).decode("utf-8", errors="replace")
    summary = _summary(jsonl)
    door2 = summary["door2"]
    post = door2.get("post") or {}
    judgement = door2.get("judgement") or {}
    return {
        "id": stamp, "at": _when(stamp), "text": text[:MAX_TEXT], "cut": len(text) > MAX_TEXT, "records": summary["records"],
        "share": str(door2.get("text") or ""), "post_url": str(post.get("url") or ""),
        "judgement": {k: str(judgement.get(k) or "") for k in ("assessment", "recommendation", "confidence")} if judgement else None,
        "errors": [str(e)[:300] for e in door2.get("errors") or []],
        "replies": [{"handle": str(r.get("handle", ""))[:64], "text": str(r.get("text", ""))[:500]}
                    for r in door2.get("replies") or [] if isinstance(r, dict)][:20],
    }
