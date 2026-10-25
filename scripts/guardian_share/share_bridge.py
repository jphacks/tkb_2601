#!/usr/bin/env python3
"""異常を人の承認で Bluesky に共有し、返信をレポートにまとめる共有中継（PSoC 6 共有パネル ⇄ PC ⇄ Bluesky）。

センサー中継の記録（Logs/sensor_bridge/*.jsonl）→ 共有待ちの異常 → PSoC 6 の SW2 を人が押す → Bluesky に投稿
→ 返信を定期的に取る → 異常ごとの JSON と Markdown / HTML のレポートを出力先に書く（Google Drive の同期フォルダでもよい）
使い方: scripts/guardian_share/README.md
"""
from __future__ import annotations

import argparse
import json
import os
import queue
import re
import signal
import sys
import threading
import time
from collections import deque
from datetime import datetime, timedelta
from pathlib import Path

from bluesky import BlueskyClient, BlueskyError, tag_facets
from report import LEVEL_WORDS, compose_post, render_html, render_markdown

REPO_ROOT = Path(__file__).resolve().parents[2]
CYPRESS_VID = 0x04B4  # KitProg3（PSoC 6 キットの書き込み器と USB シリアル）
SAMPLE_HISTORY = 30   # 異常 1 件に添える、直前の測定値の数
LAMP_INTERVAL = 0.2   # PSoC 6 にランプの点灯・消灯を送る間隔（秒）


def say(message: str) -> None:
    print(f"{datetime.now():%H:%M:%S} {message}", flush=True)


def wall_now() -> str:
    return datetime.now().isoformat(timespec="milliseconds")


def _write(path: Path, text: str) -> None:
    """一時ファイルに書いてから置き換える（Google Drive が書きかけのファイルを同期しないように）。"""
    temporary = path.with_name("." + path.name + ".tmp")
    temporary.write_text(text, encoding="utf-8")
    os.replace(temporary, path)


class Archive:
    """出力先: incidents/incident-001.json と reports/incident-001.md / .html。"""

    def __init__(self, root: Path):
        self.root = root
        self.incidents = root / "incidents"
        self.reports = root / "reports"
        self.incidents.mkdir(parents=True, exist_ok=True)
        self.reports.mkdir(parents=True, exist_ok=True)

    def _stored(self) -> list[Path]:
        return [p for p in self.incidents.glob("incident-*.json") if re.fullmatch(r"incident-\d+\.json", p.name)]

    def next_number(self) -> int:
        return max((int(p.stem.split("-")[1]) for p in self._stored()), default=0) + 1

    def save(self, incident: dict) -> None:
        _write(self.incidents / f"{incident['id']}.json", json.dumps(incident, ensure_ascii=False, indent=2))
        _write(self.reports / f"{incident['id']}.md", render_markdown(incident))
        _write(self.reports / f"{incident['id']}.html", render_html(incident))

    def shared_since(self, since: datetime) -> list[dict]:
        """前回までに Bluesky へ投稿した異常のうち、まだ返信を追う期間のもの（中継を起動し直しても追い続ける）。"""
        found = []
        for path in sorted(self._stored()):
            try:
                incident = json.loads(path.read_text(encoding="utf-8"))
                shared = incident.get("shared") or {}
                if shared.get("uri") and not shared.get("dry_run") and datetime.fromisoformat(shared["at"]) >= since:
                    found.append(incident)
            except (ValueError, KeyError, OSError):
                continue
        return found


class ShareDesk:
    """共有待ちの異常、人の承認、投稿、返信の取得、ランプの状態をまとめる。

    poster が None のときは試し運転: 承認しても投稿せず、記録とレポートだけ作る。
    """

    def __init__(self, archive: Archive, poster, tags: list[str], follow_hours: float = 6.0, log=say):
        self.archive = archive
        self.poster = poster
        self.tags = tags
        self.follow_hours = follow_hours
        self.say = log
        self.robot = "B"
        self.config = {"threshold_mg": 300.0, "warning_count": 2, "limited_count": 6}
        self.samples: deque[dict] = deque(maxlen=SAMPLE_HISTORY)
        self.pending: dict | None = None   # 人の承認を待っている異常
        self.last: dict | None = None      # 最後に承認した異常（復帰の時刻を書き足すため）
        self.unseen = 0                    # まだ人が見ていない返信の数
        self.feedback: tuple[str, float] | None = None
        self.followed: list[dict] = archive.shared_since(datetime.now() - timedelta(hours=follow_hours)) if poster else []

    # ---- センサー中継の記録 ----

    def on_log_record(self, record: dict, replay: bool = False) -> None:
        """記録 1 行を受け取る。replay は起動前からあった記録で、設定と測定値だけ読み、異常は共有待ちにしない。"""
        kind = record.get("kind")
        if kind == "start":
            self.robot = str(record.get("robot") or self.robot)
            for key in self.config:
                if isinstance(record.get(key), (int, float)):
                    self.config[key] = record[key]
            self.samples.clear()
        elif kind == "sample":
            if not record.get("warmup") and isinstance(record.get("rms_mg"), (int, float)):
                self.samples.append({"at": record.get("wall"), "rms_mg": float(record["rms_mg"]), "peak_mg": record.get("peak_mg")})
        elif replay:
            return
        elif kind == "level" and record.get("level") in ("Warning", "Limited"):
            self.raise_incident(record["level"], str(record.get("reason", "")), record.get("wall") or wall_now(), measured=True)
        elif kind == "hold":
            self.raise_incident("Hold", str(record.get("reason", "")), record.get("wall") or wall_now(), measured=True)
        elif kind == "key2" and record.get("accepted"):
            self.recovered(record.get("wall") or wall_now())

    def raise_incident(self, level: str, reason: str, at: str, measured: bool) -> dict:
        """共有待ちの異常を作る。共有する前にさらに悪くなったら、同じ異常の経過として書き足す。"""
        incident = self.pending
        if incident is None:
            number = self.archive.next_number()
            incident = {"number": number, "id": f"incident-{number:03d}", "robot": self.robot, "detected_at": at,
                        "measured": measured, "history": [], "recovered_at": None, "shared": None, "replies": [], **self.config}
            self.pending = incident
        incident["history"].append({"at": at, "level": level, "reason": reason})
        incident.update(level=level, reason=reason, updated_at=at, recovered_at=None, samples=list(self.samples))
        self.archive.save(incident)
        self.say(f"共有待ち {incident['id']}: ロボット {incident['robot']} が{LEVEL_WORDS[level]}（{reason}）。"
                 "PSoC 6 の SW2 を押すと Bluesky に共有する")
        return incident

    def recovered(self, at: str) -> None:
        for incident in (self.pending, self.last):
            if incident is not None and not incident.get("recovered_at"):
                incident["recovered_at"] = at
                self.archive.save(incident)

    def open_demo(self) -> dict:
        """リハーサル用の模擬の異常。投稿文にもレポートにも「模擬データ」と書く。"""
        base = datetime.now() - timedelta(seconds=len(DEMO_RMS))
        self.samples.clear()
        for i, rms in enumerate(DEMO_RMS):
            self.samples.append({"at": (base + timedelta(seconds=i)).isoformat(timespec="milliseconds"), "rms_mg": rms, "peak_mg": rms * 2.3})
        at = wall_now()
        self.raise_incident("Warning", "（模擬）振動 300 mg 以上が 2 回続いたため注意", at, measured=False)
        return self.raise_incident("Limited", f"（模擬）振動 300 mg 以上が 6 回続いたため制限（今回 {DEMO_RMS[-1]:.0f} mg）", at, measured=False)

    # ---- 人の承認（PSoC 6 の SW2） ----

    def approve(self, source: str, now: float) -> str:
        """ボタンが押された。共有待ちがあれば投稿し、なければ届いた返信を確認済みにする。"""
        if self.pending is not None:
            return self._share(self.pending, source, now)
        if self.unseen:
            self.unseen = 0
            self.say("返信を確認済みにした（レポートは更新済み）")
            self._flash("ok", now)
            return "seen"
        self.say(f"{source}: 共有待ちの異常はない")
        return "idle"

    def _share(self, incident: dict, source: str, now: float) -> str:
        text = compose_post(incident, self.tags)
        shared = {"dry_run": self.poster is None, "text": text, "at": wall_now(), "approved_by": source}
        if self.poster is None:
            self.say(f"{incident['id']} の共有を承認した（試し運転のため投稿はしていない）\n{text}")
        else:
            try:
                shared.update(self.poster.post(text, tag_facets(text, self.tags)))
            except (BlueskyError, OSError, ValueError, KeyError) as error:
                self.say(f"{incident['id']} を投稿できなかった: {error}。もう一度 SW2 を押すと試し直す")
                self._flash("error", now)
                return "error"
            self.followed.append(incident)
            self.say(f"{incident['id']} を Bluesky に投稿した: {shared.get('url') or shared['uri']}")
        incident["shared"] = shared
        self.pending = None
        self.last = incident
        self.archive.save(incident)
        self._flash("ok", now)
        return "shared"

    # ---- 返信 ----

    def poll_replies(self) -> int:
        """投稿した異常の返信を取り、新しいものをレポートに書き足す。新しい返信の数を返す。"""
        if self.poster is None:
            return 0
        cutoff = datetime.now() - timedelta(hours=self.follow_hours)
        added = 0
        for incident in list(self.followed):
            shared = incident["shared"]
            if datetime.fromisoformat(shared["at"]) < cutoff:
                self.followed.remove(incident)
                continue
            try:
                replies = self.poster.replies(shared["uri"])
            except (BlueskyError, OSError, ValueError, KeyError) as error:
                self.say(f"{incident['id']} の返信を取れなかった: {error}")
                continue
            known = {reply["uri"] for reply in incident["replies"]}
            # 自分のアカウントの返信は外部の意見ではないので入れない
            new = [r for r in replies if r["uri"] not in known and r["handle"] != shared.get("handle")]
            if not new:
                continue
            incident["replies"] = sorted(incident["replies"] + new, key=lambda reply: reply["created_at"])
            self.archive.save(incident)
            added += len(new)
            for reply in new:
                self.say(f"{incident['id']} に返信: @{reply['handle']}「{reply['text'][:60]}」")
        self.unseen += added
        return added

    # ---- ランプ ----

    def _flash(self, kind: str, now: float) -> None:
        self.feedback = (kind, now + (1.5 if kind == "ok" else 2.0))

    def lamp(self, now: float) -> bool:
        """PSoC 6 のランプ。共有待ちはゆっくり点滅、未確認の返信は点灯、投稿の直後は点灯、失敗は速い点滅。

        点滅の切り替えは送る間隔（0.2 秒）より長くする。同じ長さだと、時刻の端数で同じ側が続くことがある。
        """
        if self.feedback is not None and now < self.feedback[1]:
            return True if self.feedback[0] == "ok" else int(now / 0.25) % 2 == 0
        if self.pending is not None:
            return int(now / 0.5) % 2 == 0
        return self.unseen > 0


DEMO_RMS = [14.0, 12.5, 15.2, 340.6, 405.3, 388.0, 421.7, 398.4, 412.0]


class LogFollower:
    """センサー中継の記録を追う。いちばん新しいファイルを読み、中継が起動し直して新しいファイルができたら移る。

    起動時にすでにあったファイルは、設定と直近の測定値だけ読む（古い異常を共有待ちにしない）。
    """

    def __init__(self, directory: Path):
        self.directory = directory
        self.path: Path | None = None
        self._file = None
        self._buffer = ""
        self._replaying = False
        self._next_scan = 0.0
        self._existing = set(self._files())

    def _files(self) -> list[Path]:
        return sorted(self.directory.glob("*.jsonl")) if self.directory.is_dir() else []

    def _latest(self) -> Path | None:
        files = self._files()
        return files[-1] if files else None

    def poll(self, now: float) -> list[tuple[dict, bool]]:
        if now >= self._next_scan:
            self._next_scan = now + 2.0
            latest = self._latest()
            if latest is not None and latest != self.path:
                if self._file is not None:
                    self._file.close()
                self._replaying = latest in self._existing
                self.path = latest
                self._file = latest.open("r", encoding="utf-8", errors="replace")
                self._buffer = ""
        if self._file is None:
            return []
        self._buffer += self._file.read()
        *lines, self._buffer = self._buffer.split("\n")
        records = []
        for line in lines:
            try:
                record = json.loads(line)
            except ValueError:
                continue
            if isinstance(record, dict):
                records.append((record, self._replaying))
        self._replaying = False
        return records

    def close(self) -> None:
        if self._file is not None:
            self._file.close()


def find_psoc6_port() -> str | None:
    from serial.tools import list_ports

    for port in list_ports.comports():
        if port.vid == CYPRESS_VID:
            return port.device
    return None


class Psoc6Link:
    """PSoC 6 共有パネルとの USB シリアル。読むのは専用のスレッド、ランプの指示はメインから送る。"""

    def __init__(self, port_name: str | None, events: queue.Queue, stop: threading.Event):
        self._port_name = port_name
        self._events = events
        self._stop = stop
        self._device = None
        self._lock = threading.Lock()

    def run(self) -> None:
        import serial

        missing_noted = False
        while not self._stop.is_set():
            name = self._port_name or find_psoc6_port()
            if name is None:
                if not missing_noted:
                    self._events.put(("psoc_missing", None))
                    missing_noted = True
                self._stop.wait(1.0)
                continue
            missing_noted = False
            try:
                # exclusive: 同じ PSoC 6 を 2 つのプログラムで同時に開かない（Windows はもともと 1 つだけ）
                with serial.Serial(name, 115200, timeout=0.5, exclusive=True) as device:
                    with self._lock:
                        self._device = device
                    self._events.put(("psoc_open", name))
                    while not self._stop.is_set():
                        raw = device.readline()
                        line = raw.decode("utf-8", errors="replace").strip()
                        if line.startswith("{"):
                            self._events.put(("psoc_line", line))
            except (serial.SerialException, OSError) as error:
                self._events.put(("psoc_lost", f"{name}: {error}"))
            finally:
                with self._lock:
                    self._device = None
            self._stop.wait(1.0)

    def send(self, message: dict) -> None:
        data = (json.dumps(message) + "\n").encode("utf-8")
        with self._lock:
            if self._device is None:
                return
            try:
                self._device.write(data)
            except OSError:
                pass  # 読む側のスレッドが切断に気づいてつなぎ直す


def read_keyboard(events: queue.Queue, stop: threading.Event) -> None:
    """Enter キーを承認として扱う（PSoC 6 がないときのリハーサル用）。"""
    for _ in sys.stdin:
        if stop.is_set():
            return
        events.put(("key", None))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="異常を人の承認で Bluesky に共有し、返信をレポートにまとめる")
    parser.add_argument("--sensor-log-dir", type=Path, default=REPO_ROOT / "Logs" / "sensor_bridge", help="センサー中継の記録の場所")
    parser.add_argument("--out-dir", type=Path, default=REPO_ROOT / "Logs" / "guardian_share",
                        help="異常の記録とレポートの書き出し先。Google Drive for desktop の同期フォルダを指定すると Drive に保存される")
    parser.add_argument("--psoc-serial", help="PSoC 6 のシリアルポート。省略すると KitProg3 を自動で探す")
    parser.add_argument("--no-psoc", action="store_true", help="PSoC 6 を使わない（--keyboard と一緒に使う）")
    parser.add_argument("--keyboard", action="store_true", help="Enter キーでも承認する")
    parser.add_argument("--dry-run", action="store_true", help="試し運転。承認しても投稿しない")
    parser.add_argument("--demo", action="store_true", help="模擬の異常を 1 件、共有待ちにして始める（リハーサル用）")
    parser.add_argument("--hashtags", default="ロボもり,JPHACKS", help="投稿に付けるハッシュタグ（カンマ区切り）")
    parser.add_argument("--reply-interval", type=float, default=30.0, help="返信を取りに行く間隔（秒、既定: 30）")
    parser.add_argument("--follow-hours", type=float, default=6.0, help="投稿後、返信を追う時間（時間、既定: 6）")
    parser.add_argument("--service", default="https://bsky.social", help="投稿するアカウントの PDS")
    parser.add_argument("--appview", default="https://public.api.bsky.app", help="返信を読む AppView")
    return parser.parse_args()


def stop_on_terminate(signum, frame) -> None:
    raise KeyboardInterrupt


def main() -> None:
    args = parse_args()
    tags = [tag.strip().lstrip("#") for tag in args.hashtags.split(",") if tag.strip()]
    handle, password = os.environ.get("BLUESKY_HANDLE"), os.environ.get("BLUESKY_APP_PASSWORD")
    poster = None
    if args.dry_run:
        say("試し運転: 承認しても Bluesky には投稿しない")
    elif not (handle and password):
        say("BLUESKY_HANDLE と BLUESKY_APP_PASSWORD が未設定のため、試し運転（投稿しない）で動かす")
    else:
        poster = BlueskyClient(handle, password, args.service, args.appview)
        say(f"Bluesky: @{handle} で投稿する（{args.service}）")
    archive = Archive(args.out_dir)
    desk = ShareDesk(archive, poster, tags, args.follow_hours)
    follower = LogFollower(args.sensor_log_dir)
    events: queue.Queue = queue.Queue()
    stop = threading.Event()
    signal.signal(signal.SIGTERM, stop_on_terminate)
    psoc = None
    if not args.no_psoc:
        psoc = Psoc6Link(args.psoc_serial, events, stop)
        threading.Thread(target=psoc.run, name="psoc6", daemon=True).start()
    if args.keyboard:
        threading.Thread(target=read_keyboard, args=(events, stop), name="keyboard", daemon=True).start()
    say(f"センサー中継の記録: {args.sensor_log_dir}")
    say(f"書き出し先: {archive.root}")
    if desk.followed:
        say(f"前回までに投稿した {len(desk.followed)} 件の返信を引き続き追う")
    if args.demo:
        desk.open_demo()

    next_lamp = next_log = next_replies = time.monotonic()
    psoc_seen = False
    try:
        while True:
            try:
                kind, payload = events.get(timeout=0.05)
            except queue.Empty:
                kind, payload = None, None
            now = time.monotonic()
            if kind == "psoc_line":
                try:
                    message = json.loads(payload)
                except ValueError:
                    message = {}
                if message.get("type") == "event" and message.get("event") == "button":
                    desk.approve("PSoC 6 の SW2", now)
                elif (message.get("event") == "hello" or message.get("type") == "hello") and not psoc_seen:
                    psoc_seen = True
                    say(f"PSoC 6 の共有パネルを確認（{message.get('fw', '?')}）")
            elif kind == "key":
                desk.approve("Enter キー", now)
            elif kind == "psoc_open":
                psoc_seen = False
                say(f"PSoC 6 に接続: {payload}")
            elif kind == "psoc_lost":
                say(f"PSoC 6 との接続が切れた: {payload}")
            elif kind == "psoc_missing":
                say("PSoC 6 が見つからない。USB（KitProg3 側）をつなぐと自動で接続する")

            if now >= next_log:
                next_log = now + 0.25
                for record, replay in follower.poll(now):
                    desk.on_log_record(record, replay)
            if psoc is not None and now >= next_lamp:
                next_lamp = now + LAMP_INTERVAL
                psoc.send({"cmd": "lamp", "on": desk.lamp(now)})
            if now >= next_replies:
                next_replies = now + args.reply_interval
                desk.poll_replies()
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        follower.close()
        say("共有中継を終了")


if __name__ == "__main__":
    main()
