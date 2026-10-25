#!/usr/bin/env python3
"""監視ハブ: 監視 AI と、対応する人の推薦と、扉 2（店内 ⇄ 外部）。

Unity（GuardianHubLink）が 127.0.0.1:47612 に接続し、0.5 秒ごとにロボットの様子（telemetry）を送ってくる。
ハブはそれを監視 AI に渡し、速度の指示・戻す提案・対応する人の推薦（advice）を返す。
インターネットには出ない。外に出すのは、扉 2 を人が開けたときに Google Drive の同期フォルダへ書く記録だけ。
使い方: scripts/guardian_hub/README.md
"""
from __future__ import annotations

import argparse
import json
import os
import queue
import signal
import socket
import sys
import threading
import time
import uuid
from datetime import datetime
from pathlib import Path

from dispatch import Failure, classify, recommend
from monitor import Monitor
from protocol import MAX_LINE, Rejected, build_advice, decode, encode, parse_scene, parse_telemetry

REPO_ROOT = Path(__file__).resolve().parents[2]
ADVICE_INTERVAL = 0.5


def say(message: str) -> None:
    print(f"{datetime.now():%H:%M:%S} {message}", flush=True)


def write_replacing(path: Path, text: str, attempts: int = 5) -> None:
    """書き終えてから置き換える。Windows では同期ソフトやウイルス対策が掴んでいることがあるので、少し待って試し直す。"""
    temporary = path.with_name("." + path.name + ".tmp")
    temporary.write_text(text, encoding="utf-8")
    for attempt in range(attempts):
        try:
            os.replace(temporary, path)
            return
        except PermissionError:
            if attempt == attempts - 1:
                raise
            time.sleep(0.2 * (attempt + 1))


class Hub:
    """ソケットを持たない中身。行を受け取って返事の行を返すので、そのままテストできる。"""

    def __init__(self, monitor: Monitor, log=say, records: list[dict] | None = None):
        self.monitor = monitor
        self.say = log
        self.boot = f"hub-{datetime.now():%Y%m%d-%H%M%S}-{uuid.uuid4().hex[:4]}"
        self.seq = 0
        self.gate = "store"
        self.door = "store"
        self.telemetry: dict | None = None
        self.unity_boot = ""
        self.last_seq = -1
        self.rejected = 0
        self.last_moving: dict[str, float] = {}
        self.failures: dict[str, Failure] = {}
        self.dispatch: list[dict] = []
        self.records = records if records is not None else []   # 外に出すまで貯める記録
        self._shown: dict[str, str] = {}
        self.scene: dict | None = None   # Unity のシーンから読んだ店内の配置（最新）
        self.scene_rev = 0               # 配置を受け取るたびに 1 増える（画面が描き直すかを決める）
        self._talk_seen: tuple[str, int] | None = None

    def record(self, kind: str, **fields) -> None:
        self.records.append({"at": datetime.now().isoformat(timespec="seconds"), "kind": kind, **fields})

    def on_line(self, raw: bytes | str, now: float) -> bool:
        """Unity からの 1 行。使えた行なら True。使えない行は数えて捨てる（止まらない）。"""
        try:
            message = decode(raw)
            if message.get("type") == "scene":
                return self._on_scene(parse_scene(message))
            telemetry = parse_telemetry(message)
        except Rejected as error:
            self.rejected += 1
            if self.rejected <= 5 or self.rejected % 100 == 0:
                self.say(f"Unity からの行を 1 つ捨てた（{error}。累計 {self.rejected}）")
            return False
        if telemetry["boot"] == self.unity_boot and telemetry["seq"] <= self.last_seq:
            self.rejected += 1
            return False  # 古い行の再送
        if telemetry["boot"] != self.unity_boot:
            self.unity_boot = telemetry["boot"]
            self.say(f"Unity の新しい起動を確認（{telemetry['boot'] or '名前なし'}）")
        self.last_seq = telemetry["seq"]
        if telemetry["door"] != self.door:
            self.door = telemetry["door"]
            self.record("door", door=self.door)
            self.say("扉 1（Unity）: " + ("単独。Unity は外からの指示を受けていない" if self.door == "standalone" else "店内"))
        self.telemetry = telemetry
        talk = telemetry.get("talk")
        if talk and self._talk_seen != (telemetry["boot"], talk["n"]):
            first = self._talk_seen is None or self._talk_seen[0] != telemetry["boot"]
            self._talk_seen = (telemetry["boot"], talk["n"])
            if not first or talk["n"] == 1:   # つなぎ直した直後は、前に話した分を重ねて記録しない
                self.record("talk", robot=talk["robot"], question=talk["question"], answer=talk["answer"], by=talk["by"],
                            text=f"{talk['by'] or '人'}「{talk['question']}」→ ロボット {talk['robot']}「{talk['answer']}」")
        self.monitor.observe(telemetry, now)
        for event in self.monitor.take_events():
            self.record("ai", text=event)
            self.say("監視AI " + event)
        self._update_dispatch(telemetry, now)
        return True

    def _on_scene(self, scene: dict) -> bool:
        changed = self.scene is None or {k: v for k, v in self.scene.items() if k != "seq"} != {k: v for k, v in scene.items() if k != "seq"}
        self.scene = scene
        if changed:
            self.scene_rev += 1
            kinds = sorted({t["kind"] for t in scene["things"]})
            self.say(f"Unity のシーンから店内の配置を読んだ（物 {len(scene['things'])} 個: {'・'.join(kinds)}）")
        return True

    def _update_dispatch(self, telemetry: dict, now: float) -> None:
        seen = set()
        for robot in telemetry["robots"]:
            # 止めて（保持して）いる間は「動けない」の時計を進めない。止めを解いた直後に、動けないと誤って呼ばないため
            if robot["speed"] > 0.05 or not robot["carrying"] or robot["stopped"] or robot["id"] not in self.last_moving:
                self.last_moving[robot["id"]] = now
            kind = classify(robot, now - self.last_moving.get(robot["id"], now))
            if kind is None:
                continue
            seen.add(robot["id"])
            failure = self.failures.get(robot["id"])
            if failure is None or failure.kind != kind:
                failure = self.failures[robot["id"]] = Failure(robot["id"], kind, robot["x"], robot["z"], now)
            failure.x, failure.z = robot["x"], robot["z"]
        for robot_id in list(self.failures):
            if robot_id not in seen:
                del self.failures[robot_id]
                self._shown.pop(robot_id, None)
                self.say(f"ロボット {robot_id} は故障の状態を抜けた")
        self.dispatch = []
        for failure in self.failures.values():
            choice = recommend(failure, telemetry["people"], self.monitor.hazards())
            if choice is None:
                continue
            self.dispatch.append(choice)
            summary = f"{choice['person']}（約 {choice['eta_s']:.0f} 秒、{'→'.join(choice['route'])}）"
            if self._shown.get(failure.robot) != summary:
                self._shown[failure.robot] = summary
                self.record("dispatch", **choice)
                self.say(f"推薦: ロボット {failure.robot} が{choice['situation']} → {summary}"
                         + (f"。次点は {choice['backup']}" if choice["backup"] else ""))

    def advice(self, now: float) -> bytes:
        self.seq += 1
        robots, zones, proposals = self.monitor.advice()
        return encode(build_advice(self.boot, self.seq, self.gate, self.monitor.learning(now), robots, zones, proposals, self.dispatch))

    def drill_lines(self) -> list[bytes]:
        """扉 1 の訓練（模擬）。Unity が捨てるはずの助言を 3 種類つくる。10 秒に 3 回捨てると、Unity は自動で扉 1 を閉じる。

        1. 速度を上げる指示（ネットワークからは下げることしかできない）
        2. 順番が戻った古い助言
        3. 途中で切れた JSON
        """
        robots = (self.telemetry or {}).get("robots") or [{"id": "A"}]
        _, zones, _ = self.monitor.advice()
        self.seq += 1
        raise_speed = build_advice(self.boot, self.seq, self.gate, False,
                                   [{"id": robots[0]["id"], "speed_scale": 1.5, "reason": "模擬: 速度を上げる指示"}], zones, [], [])
        stale = build_advice(self.boot, max(0, self.seq - 10), self.gate, False, [], zones, [], [])
        broken = ('{"v":1,"type":"advice","boot":"' + self.boot + '","seq":\n').encode("utf-8")
        self.record("drill", text="扉 1 の訓練（模擬）: 使えない助言を 3 回送った")
        return [encode(raise_speed), encode(stale), broken]

    def export(self, directory: Path) -> Path:
        """扉 2 を開けたときに外へ出す記録。JSON Lines と Markdown。書いたら記録を空にする。"""
        directory.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        lines = "".join(json.dumps(item, ensure_ascii=False) + "\n" for item in self.records)
        write_replacing(directory / f"guardian-records-{stamp}.jsonl", lines)
        markdown = [f"# 監視ハブの記録 {stamp}", "", f"件数: {len(self.records)}", ""]
        for item in self.records:
            text = item.get("text") or (f"ロボット {item.get('robot')}: {item.get('situation')} → {item.get('person')}"
                                        f"（約 {item.get('eta_s')} 秒）" if item["kind"] == "dispatch" else item.get("door", ""))
            markdown.append(f"- {item['at']} [{item['kind']}] {text}")
        path = directory / f"guardian-records-{stamp}.md"
        write_replacing(path, "\n".join(markdown) + "\n")
        self.records.clear()
        return path


class UnityServer:
    """Unity が 127.0.0.1 に接続してくるのを待つ。接続は 1 本だけで、新しく来たら古いほうを閉じる。"""

    def __init__(self, port: int, events: queue.Queue, stop: threading.Event, host: str = "127.0.0.1"):
        """host は既定で 127.0.0.1（この PC の中だけ）。Docker の中核では、閉じたネットワークの中で 0.0.0.0 を使う。"""
        self._events, self._stop = events, stop
        self._client: socket.socket | None = None
        self._lock = threading.Lock()
        self._server = socket.create_server((host, port))
        self._server.settimeout(0.5)
        self._accepting = threading.Event()
        self._accepting.set()

    def set_accepting(self, accepting: bool) -> None:
        """False にすると、今の接続を切り、新しい接続もすぐ閉じる（時間割の「単独」）。既定は受ける。"""
        if accepting:
            self._accepting.set()
            return
        self._accepting.clear()
        with self._lock:
            client, self._client = self._client, None
        if client is not None:
            client.close()   # 読む側のスレッドが気づいて unity_lost を出す

    def accept_forever(self) -> None:
        while not self._stop.is_set():
            try:
                client, address = self._server.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            if not self._accepting.is_set():
                client.close()
                continue
            client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            client.settimeout(1.0)
            with self._lock:
                if self._client is not None:
                    self._client.close()
                self._client = client
            self._events.put(("unity_connected", f"{address[0]}:{address[1]}"))
            threading.Thread(target=self._read, args=(client,), name="unity-read", daemon=True).start()

    def _read(self, client: socket.socket) -> None:
        buffer = b""
        while not self._stop.is_set():
            try:
                chunk = client.recv(65536)
            except socket.timeout:
                continue
            except OSError:
                break
            if not chunk:
                break
            buffer += chunk
            *lines, buffer = buffer.split(b"\n")
            for line in lines:
                if line.strip():
                    self._events.put(("line", line))
            if len(buffer) > MAX_LINE:
                buffer = b""   # 改行のない長すぎるデータは捨てる
                self._events.put(("line", b"x" * (MAX_LINE + 1)))
        with self._lock:
            if self._client is client:
                self._client = None
        client.close()
        self._events.put(("unity_lost", None))

    @property
    def connected(self) -> bool:
        with self._lock:
            return self._client is not None

    def send(self, data: bytes) -> None:
        with self._lock:
            client = self._client
        if client is None:
            return
        try:
            client.sendall(data)
        except OSError:
            with self._lock:
                if self._client is client:
                    self._client = None
            client.close()

    def close(self) -> None:
        self._server.close()
        with self._lock:
            if self._client is not None:
                self._client.close()


def read_commands(events: queue.Queue, stop: threading.Event) -> None:
    for line in sys.stdin:
        if stop.is_set():
            return
        events.put(("command", line.strip().lower()))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="監視 AI と、対応する人の推薦と、扉 2（店内 ⇄ 外部）")
    parser.add_argument("--port", type=int, default=47612, help="Unity が接続してくる TCP ポート（127.0.0.1 だけ。既定: 47612）")
    parser.add_argument("--drive-dir", type=Path, help="扉 2 を開けたときに記録を書く場所（Google Drive for desktop の同期フォルダ）")
    parser.add_argument("--external-minutes", type=float, default=5.0, help="扉 2 を開けておく最長の時間（分、既定: 5）")
    parser.add_argument("--learn-seconds", type=float, default=20.0, help="起動してから、ふだんの揺れを学ぶだけの時間（秒）")
    parser.add_argument("--relax-after", type=float, default=120.0, help="揺れがこれだけ続かなければ、元に戻す提案を出す（秒）")
    parser.add_argument("--simulate-bump", action="append", default=[], metavar="場所",
                        help="この場所に段差がある模擬（例: 通路中）。記録と理由に「模擬」と書く")
    parser.add_argument("--log-dir", type=Path, default=REPO_ROOT / "Logs" / "guardian_hub", help="ハブの記録の保存先")
    return parser.parse_args()


def stop_on_terminate(signum, frame) -> None:
    raise KeyboardInterrupt


def main() -> None:
    # Windows のコンソールでも日本語で落ちないように
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    args = parse_args()
    events: queue.Queue = queue.Queue()
    stop = threading.Event()
    try:
        server = UnityServer(args.port, events, stop)
    except OSError as error:
        sys.exit(f"127.0.0.1:{args.port} を使えない（{error}）。別のハブが動いていないか確かめる")
    signal.signal(signal.SIGTERM, stop_on_terminate)
    args.log_dir.mkdir(parents=True, exist_ok=True)
    log_path = args.log_dir / datetime.now().strftime("%Y%m%d-%H%M%S.jsonl")
    log_file = log_path.open("a", encoding="utf-8")

    def logged_say(message: str) -> None:
        say(message)
        log_file.write(json.dumps({"at": datetime.now().isoformat(timespec="milliseconds"), "text": message}, ensure_ascii=False) + "\n")
        log_file.flush()

    hub = Hub(Monitor(args.learn_seconds, relax_after=args.relax_after, simulated_bumps=tuple(args.simulate_bump)), logged_say)
    threading.Thread(target=server.accept_forever, name="unity", daemon=True).start()
    threading.Thread(target=read_commands, args=(events, stop), name="commands", daemon=True).start()
    logged_say(f"監視ハブを開始: Unity 待ち受け 127.0.0.1:{args.port}（インターネットには出ない）")
    logged_say(f"記録: {log_path}")
    logged_say("コマンド: open = 扉 2 を開けて記録を外に書き出す / close = 閉じる / drill = 扉 1 の訓練（模擬） / status = 状態")
    if args.simulate_bump:
        logged_say(f"模擬の段差: {'・'.join(args.simulate_bump)}")

    external_until, close_reason = 0.0, "時間切れ"
    scheduled: list[tuple[float, bytes]] = []   # 扉 1 の訓練で、時間をずらして送る行
    drill_at = -1e9
    next_advice = time.monotonic()
    try:
        while True:
            try:
                kind, payload = events.get(timeout=0.05)
            except queue.Empty:
                kind, payload = None, None
            now = time.monotonic()
            if kind == "line":
                hub.on_line(payload, now)
            elif kind == "unity_connected":
                logged_say(f"Unity が接続した（{payload}）")
            elif kind == "unity_lost":
                scheduled.clear()
                if now - drill_at < 5:
                    logged_say("Unity が接続を切った。扉 1 が自動で閉じた（訓練どおり）。Unity で人が「店内に戻す」を押すまで、つながらない")
                else:
                    logged_say("Unity との接続が切れた。つなぎ直しを待つ")
            elif kind == "command":
                if payload in ("open", "o"):
                    if args.drive_dir is None:
                        logged_say("--drive-dir が指定されていないので、扉 2 は開けない")
                    else:
                        hub.gate, external_until, close_reason = "external", now + args.external_minutes * 60, "時間切れ"
                        logged_say("扉 2 を開けた（人の操作）。記録を書き出したら自動で閉じる")
                        try:
                            path = hub.export(args.drive_dir)
                            logged_say(f"書き出した: {path}")
                            # 開いていたことが Unity の画面にも出るよう、少しだけ開けておいてから閉じる
                            external_until, close_reason = now + 3.0, "書き出しが終わったため"
                        except OSError as error:
                            logged_say(f"書き出せなかった（{error}）。{args.external_minutes:g} 分で自動で閉じる")
                elif payload in ("close", "c"):
                    hub.gate = "store"
                    logged_say("扉 2 を閉じた（人の操作）")
                elif payload in ("drill", "d"):
                    if not server.connected or hub.telemetry is None:
                        logged_say("Unity がつながっていないので、扉 1 の訓練はできない")
                    else:
                        scheduled = [(now + 0.4 * k, line) for k, line in enumerate(hub.drill_lines())]
                        drill_at = now
                        logged_say("扉 1 の訓練（模擬）: 使えない助言を 0.4 秒おきに 3 回送る。Unity はそれを捨て、自動で扉 1 を閉じるはず")
                elif payload in ("status", "s"):
                    robots, zones, proposals = hub.monitor.advice()
                    logged_say(f"扉 1 {hub.door} / 扉 2 {hub.gate} / 速度を下げたロボット {len(robots)} 台・場所 {len(zones)} か所 / "
                               f"提案 {len(proposals)} 件 / 推薦 {len(hub.dispatch)} 件 / 捨てた行 {hub.rejected}")
            while scheduled and scheduled[0][0] <= now:
                server.send(scheduled.pop(0)[1])
            if hub.gate == "external" and now >= external_until:
                hub.gate = "store"
                logged_say(f"扉 2 を閉じた（{close_reason}、自動）")
            if now >= next_advice:
                next_advice = now + ADVICE_INTERVAL
                if server.connected and hub.telemetry is not None:
                    server.send(hub.advice(now))
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        server.close()
        logged_say("監視ハブを終了")
        log_file.close()


if __name__ == "__main__":
    main()
