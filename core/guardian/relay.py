"""センサー中継（scripts/sensor_bridge）の記録を、追記された分だけ読む。中継には何も送らず、変えもしない。

中継は Logs/sensor_bridge/ に起動ごとに 1 ファイル（JSON Lines）を書く。中核はそれを読み取り専用で見て、
監査ログと信頼状態（測定値が届いているか、途切れ、KEY2、読めない行）にする。
"""
from __future__ import annotations

import json
from datetime import datetime
from pathlib import Path

GAP_SECONDS = 3.0      # 測定値の間がこれより空いたら「途切れ」
NOTABLE = {"start", "stop", "level", "hold", "key2", "serial_open", "serial_lost", "serial_missing",
           "unity_connected", "unity_disconnected", "unity_lost"}
LEVEL_WORDS = {"Normal": "正常", "Warning": "注意", "Limited": "制限", "Hold": "保持"}
RECENT = 60            # 画面のグラフに出す、直近の測定値の数（1 秒に 1 件なら 1 分）
MAX_LINE = 16384


class RelayTail:
    def __init__(self, directory: Path):
        self.directory = directory
        self.path: Path | None = None
        self.offset = 0
        self.buffer = b""
        self.last_wall: datetime | None = None

    def _latest(self) -> Path | None:
        try:
            files = sorted(self.directory.glob("*.jsonl"))
        except OSError:
            return None
        return files[-1] if files else None

    def poll(self) -> tuple[list[dict], bool]:
        """新しく読めた記録と、新しいファイルに切り替わったかを返す。読めない行は {"kind": "unreadable"} にする。"""
        latest = self._latest()
        switched = False
        if latest is not None and latest != self.path:
            self.path, self.offset, self.buffer, switched = latest, 0, b"", True
        if self.path is None:
            return [], False
        try:
            with self.path.open("rb") as file:
                file.seek(self.offset)
                chunk = file.read(1 << 20)
        except OSError:
            return [], switched
        self.offset += len(chunk)
        self.buffer += chunk
        *lines, self.buffer = self.buffer.split(b"\n")
        if len(self.buffer) > MAX_LINE:
            self.buffer = b""
        records = []
        for raw in lines:
            text = raw.decode("utf-8", errors="replace").strip()
            if not text:
                continue
            try:
                record = json.loads(text)
            except ValueError:
                record = None
            records.append(record if isinstance(record, dict) else {"kind": "unreadable"})
        return records, switched

    def gap(self, record: dict) -> float | None:
        """測定値どうしの間隔（秒）。中継の時計（wall）で比べるので、コンテナの時刻とずれていてもよい。"""
        try:
            wall = datetime.fromisoformat(record.get("wall", ""))
        except (TypeError, ValueError):
            return None
        previous, self.last_wall = self.last_wall, wall
        return None if previous is None else (wall - previous).total_seconds()


def describe(record: dict) -> str:
    kind = record.get("kind", "")
    if kind == "level":
        level = LEVEL_WORDS.get(record.get("level"), str(record.get("level")))
        return f"中継が{level}にした" + ("（保持）" if record.get("hold") else "") + f": {record.get('reason', '')}"
    if kind == "hold":
        return f"中継が保持（Hold）にした: {record.get('reason', '')}"
    if kind == "key2":
        return "KEY2: " + ("人が戻した" if record.get("accepted") else "受け付けなかった") + f"（{record.get('reason', '')}）"
    if kind == "start":
        return f"中継が起動した（対象 {record.get('robot', '?')}）"
    if kind == "stop":
        return "中継が止まった"
    if kind == "serial_open":
        return f"StickS3 につながった（{record.get('port', '')}）"
    if kind in ("serial_lost", "serial_missing"):
        return "StickS3 とつながっていない"
    if kind == "unity_connected":
        return "Unity が中継につながった"
    if kind in ("unity_disconnected", "unity_lost"):
        return "Unity と中継が切れた"
    return kind
