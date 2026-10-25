"""時間割（人が前もって決める）。時刻で、扉 1・扉 2 をどこまで開けてよいかが決まる。

閉じる側は時間割が自動で行い、開ける側は「人が決めた時間割」か「その場の SW2」だけにする。
区間は日をまたいでもよい（例 21:10-09:00）。どの区間にも入らない時刻は DEFAULT_MODE。
"""
from __future__ import annotations

from datetime import datetime, time, timedelta

MODES = {
    "standalone": ("単独", "ハブは Unity の接続を受けない（扉1 閉）。扉2 も閉"),
    "store": ("店内（営業中）", "監視AI が助言する。扉2 は閉で、SW2 でも開かない"),
    "share": ("店内・共有可", "監視AI が助言する。SW2 で扉2 を開けられる"),
    "export": ("書き出し", "区間の始めに 1 回、Drive にだけ書き出す（Gemini・Bluesky には送らない）。SW2 でも開けられる"),
}
DOOR2_ALLOWED = {"share", "export"}
DEFAULT_MODE = "share"


def contains(start: time, end: time, moment: time) -> bool:
    if start == end:
        return True                      # 一日中
    if start < end:
        return start <= moment < end
    return moment >= start or moment < end   # 日をまたぐ


def current(now: datetime, slots: list) -> tuple[object | None, str]:
    """いまの区間（なければ None）と、その mode。先に登録した区間を優先する。"""
    for slot in slots:
        if contains(slot.start, slot.end, now.time()):
            return slot, slot.mode
    return None, DEFAULT_MODE


def occurrence(now: datetime, slot) -> str:
    """区間のこの回を表すキー（日をまたぐ区間は、始まった日で数える）。書き出しを 1 回だけにするのに使う。"""
    day = now.date()
    if slot.start > slot.end and now.time() < slot.end:
        day -= timedelta(days=1)
    return f"{day.isoformat()}#{slot.pk}"


def next_change(now: datetime, slots: list) -> tuple[datetime, str] | None:
    """次に mode が変わる時刻と、変わった先の mode（24 時間以内に変わらなければ None）。"""
    _, mode = current(now, slots)
    boundaries = set()
    for slot in slots:
        for edge in (slot.start, slot.end):
            for day in (0, 1):
                moment = datetime.combine(now.date() + timedelta(days=day), edge, tzinfo=now.tzinfo)
                if now < moment <= now + timedelta(days=1):
                    boundaries.add(moment)
    for moment in sorted(boundaries):
        _, after = current(moment, slots)
        if after != mode:
            return moment, after
    return None


def parse(spec: str) -> list[tuple[time, time, str]]:
    """「09:00-21:00 store, 21:00-21:10 export」の形を読む。24:00 は日の終わり。"""
    slots = []
    for part in [p.strip() for p in spec.replace("、", ",").split(",") if p.strip()]:
        try:
            span, mode = part.split()
            first, last = span.split("-")
        except ValueError:
            raise ValueError(f"「{part}」が読めない（例: 09:00-21:00 store）") from None
        if mode not in MODES:
            raise ValueError(f"mode は {', '.join(MODES)} のどれか（「{mode}」）")
        slots.append((_clock(first), _clock(last), mode))
    return slots


def _clock(text: str) -> time:
    if text == "24:00":
        return time(0, 0)
    try:
        hour, minute = text.split(":")
        return time(int(hour), int(minute))
    except ValueError:
        raise ValueError(f"時刻は HH:MM（「{text}」）") from None
