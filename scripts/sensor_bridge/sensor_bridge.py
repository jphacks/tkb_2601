#!/usr/bin/env python3
"""M5StickS3 の振動から Unity のロボット 1 台の安全レベルを決めて送るセンサー中継。

StickS3（USB シリアル、JSON Lines）→ 判定 → TCP 127.0.0.1:47610（GuardianNetworkBridge の形式）
要件: docs/STICKS3_UNITY_LINK.md
"""
from __future__ import annotations

import argparse
import json
import queue
import signal
import socket
import sys
import threading
import time
import uuid
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

LEVELS = ("Normal", "Warning", "Limited")
LEVEL_WORDS = {"Normal": "正常", "Warning": "注意", "Limited": "制限"}
ESPRESSIF_VID = 0x303A
# シリアルを開いた直後は StickS3 の処理が一瞬止まり、集計の時間が伸びて振動が大きく出る（実測 1663 ms で 90 mg）。
# この回数分は判定に使わない。
WARMUP_SAMPLES = 2
REPO_ROOT = Path(__file__).resolve().parents[2]


@dataclass
class JudgeConfig:
    threshold_mg: float = 300.0
    warning_count: int = 2
    limited_count: int = 6
    stale_min_s: float = 2.5
    stale_windows: float = 3.0


class Judge:
    """振動から安全レベルを決める。上げるのは自動、下げるのは KEY2（人）だけ。

    しきい値は 1 つで、それを超えた状態が何回続いたかで注意と制限を分ける。
    データが途切れたら Hold にし、データが戻っても KEY2 を押すまで Hold のまま。
    起動直後は Normal で、最初のデータが届くまでは Unity に何も送らない（has_data）。
    Hold を送ると Unity が人の判断待ちで止めるため、起動のたびに止めないようにしている。
    """

    def __init__(self, config: JudgeConfig):
        if config.threshold_mg <= 0 or not 1 <= config.warning_count < config.limited_count:
            raise ValueError("しきい値は 0 より大きく、回数は 1 ≦ 注意 < 制限 にする")
        self.config = config
        self.level = "Normal"
        self.hold = False
        self.reason = "センサーのデータを受信中"
        self.last_sample_at: float | None = None
        self.last_rms_mg: float | None = None
        self.window_s = 1.0
        self.over_count = 0

    def stale_after(self) -> float:
        return max(self.config.stale_min_s, self.config.stale_windows * self.window_s)

    @property
    def has_data(self) -> bool:
        return self.last_sample_at is not None

    def fresh(self, now: float) -> bool:
        return self.has_data and now - self.last_sample_at <= self.stale_after()

    def note_alive(self, window_ms: float | None, now: float) -> None:
        """データが届いたことだけを記録する（判定には使わない値のとき）。"""
        self.last_sample_at = now
        if window_ms and window_ms > 0:
            self.window_s = window_ms / 1000

    def on_sample(self, rms_mg: float, window_ms: float | None, now: float) -> str | None:
        """測定値を 1 つ受け取る。レベルが上がったら、その理由を返す。"""
        self.note_alive(window_ms, now)
        self.last_rms_mg = rms_mg
        self.over_count = self.over_count + 1 if rms_mg >= self.config.threshold_mg else 0
        target = ("Limited" if self.over_count >= self.config.limited_count
                  else "Warning" if self.over_count >= self.config.warning_count else None)
        if target is None or LEVELS.index(target) <= LEVELS.index(self.level):
            return None
        self.level = target
        message = f"振動 {self.config.threshold_mg:g} mg 以上が {self.over_count} 回続いたため{LEVEL_WORDS[target]}（今回 {rms_mg:.0f} mg）"
        if not self.hold:
            self.reason = message
        return message

    def tick(self, now: float) -> str | None:
        """データの途切れを調べる。新しく Hold にしたら、その理由を返す。"""
        if self.hold or not self.has_data or self.fresh(now):
            return None
        self.hold = True
        self.reason = f"センサーのデータが {now - self.last_sample_at:.1f} 秒途切れたため保持"
        return self.reason

    def on_key2(self, now: float) -> tuple[bool, str]:
        """人の復帰操作。データが届いているときだけ Normal に戻し、Hold を解く。"""
        if not self.fresh(now):
            return False, "センサーのデータが届いていないため、KEY2 の復帰を受け付けない"
        self.level = "Normal"
        self.hold = False
        self.over_count = 0
        self.reason = "KEY2 で人が復帰"
        return True, self.reason

    def robot_state(self, robot_id: str, now: float) -> dict:
        return {
            "id": robot_id,
            "final": "Hold" if self.hold else self.level,
            "capability": self.level,
            "trust": "Trusted" if self.fresh(now) else "Untrusted",
            "measured": True,
            "reasons": [self.reason],
        }


class EventLog:
    """受信時刻付きで 1 行 1 件の JSON を残す（F-60）。"""

    def __init__(self, directory: Path):
        directory.mkdir(parents=True, exist_ok=True)
        self.path = directory / datetime.now().strftime("%Y%m%d-%H%M%S.jsonl")
        self._file = self.path.open("a", encoding="utf-8")
        self._lock = threading.Lock()

    def write(self, kind: str, mono: float, **fields) -> None:
        record = {"wall": datetime.now().isoformat(timespec="milliseconds"), "mono": round(mono, 3), "kind": kind, **fields}
        with self._lock:
            self._file.write(json.dumps(record, ensure_ascii=False) + "\n")
            self._file.flush()

    def close(self) -> None:
        with self._lock:
            self._file.close()


def find_sticks3_port() -> str | None:
    from serial.tools import list_ports

    for port in list_ports.comports():
        if port.vid == ESPRESSIF_VID:
            return port.device
    return None


def read_serial(port_name: str | None, events: queue.Queue, stop: threading.Event) -> None:
    """StickS3 の行を読み続ける。抜けたら 1 秒ごとに探し直す（N-04）。"""
    import serial

    while not stop.is_set():
        name = port_name or find_sticks3_port()
        if name is None:
            events.put(("serial_missing", None, time.monotonic()))
            stop.wait(1.0)
            continue
        try:
            with serial.Serial(name, 115200, timeout=0.5) as device:
                events.put(("serial_open", name, time.monotonic()))
                while not stop.is_set():
                    raw = device.readline()
                    if not raw:
                        continue
                    line = raw.decode("utf-8", errors="replace").strip()
                    if line.startswith("{"):
                        events.put(("line", line, time.monotonic()))
        except (serial.SerialException, OSError) as error:
            events.put(("serial_lost", f"{name}: {error}", time.monotonic()))
        stop.wait(1.0)


# 段階の確かめ（--simulate）: StickS3 と同じ形の行を作って流す。StickS3 本体には何もしない。
# 静か → 強い揺れが続く（2 回で注意、6 回で制限）→ 静か（上げたら下がらない）→ KEY2（人が戻す）→ 静か
SIM_STEP_S = 0.5
SIM_PLAN = (("静か（正常のまま）", 50.0, 6), ("強い揺れ（注意 → 制限）", 600.0, 8), ("静か（制限のまま。下げるのは人）", 50.0, 6),
            ("KEY2（人が正常に戻す）", None, 1), ("静か（正常）", 50.0, 6))


def simulate_serial(events: queue.Queue, stop: threading.Event, loop: bool) -> None:
    """--simulate のとき read_serial の代わりに動く。"""
    events.put(("serial_open", "simulated", time.monotonic()))
    seq = 0
    while not stop.is_set():
        for title, mg, count in SIM_PLAN:
            events.put(("sim_phase", title, time.monotonic()))
            for _ in range(count):
                if stop.wait(SIM_STEP_S):
                    return
                seq += 1
                if mg is None:
                    line = {"type": "event", "event": "button", "btn": "KEY2", "seq": seq}
                else:
                    line = {"type": "sample", "vib_rms_g": mg / 1000, "vib_peak_g": mg / 1000 * 1.4,
                            "win_ms": int(SIM_STEP_S * 1000), "dev": "simulated", "seq": seq}
                events.put(("line", json.dumps(line), time.monotonic()))
        events.put(("sim_cycle", None, time.monotonic()))
        if not loop:
            while not stop.wait(SIM_STEP_S):   # 終わったあとも、データが途切れて Hold にならないよう静かな値を送り続ける
                seq += 1
                line = {"type": "sample", "vib_rms_g": 0.05, "win_ms": int(SIM_STEP_S * 1000), "dev": "simulated", "seq": seq}
                events.put(("line", json.dumps(line), time.monotonic()))
            return


class UnityServer:
    """GuardianNetworkBridge が 127.0.0.1 に接続してくるのを待ち、状態を送る。"""

    def __init__(self, port: int, events: queue.Queue, stop: threading.Event):
        self._clients: list[socket.socket] = []
        self._lock = threading.Lock()
        self._events = events
        self._stop = stop
        self._server = socket.create_server(("127.0.0.1", port))
        self._server.settimeout(0.5)

    def accept_forever(self) -> None:
        while not self._stop.is_set():
            try:
                client, address = self._server.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            with self._lock:
                self._clients.append(client)
            self._events.put(("unity_connected", f"{address[0]}:{address[1]}", time.monotonic()))

    def send(self, packet: dict) -> None:
        data = (json.dumps(packet, ensure_ascii=False) + "\n").encode("utf-8")
        with self._lock:
            for client in list(self._clients):
                try:
                    client.sendall(data)
                except OSError:
                    self._clients.remove(client)
                    client.close()
                    self._events.put(("unity_disconnected", None, time.monotonic()))

    @property
    def client_count(self) -> int:
        with self._lock:
            return len(self._clients)

    def close(self) -> None:
        self._server.close()
        with self._lock:
            for client in self._clients:
                client.close()
            self._clients.clear()


UNITY_PORT = 47610
# 47610 のあたりを、ほかのプログラムが押さえている PC がある。そのときは、この番号で受ける（Unity の
# GuardianNetworkBridge も、47610 につながらなければこちらを試す）
FALLBACK_PORT = 18610


def open_unity_server(port, events: queue.Queue, stop: threading.Event):
    """Unity を待ち受ける。番号を指定されたらその番号だけ。指定がなければ 47610、使えなければ 18610。"""
    if port is not None:
        return UnityServer(port, events, stop), port
    try:
        return UnityServer(UNITY_PORT, events, stop), UNITY_PORT
    except OSError as error:
        say(f"127.0.0.1:{UNITY_PORT} を使えない（{error.strerror or error}）。{FALLBACK_PORT} で待ち受ける")
        try:
            return UnityServer(FALLBACK_PORT, events, stop), FALLBACK_PORT
        except OSError as fallback:
            raise OSError(fallback.errno, f"{FALLBACK_PORT} も使えない（{fallback.strerror or fallback}）。前に起動した中継が動いていないか確かめる") from None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="M5StickS3 の振動から Unity のロボットの安全レベルを決めて送る")
    parser.add_argument("--robot", default="B", help="安全レベルを変えるロボットの ID（既定: B）")
    parser.add_argument("--serial", help="StickS3 のシリアルポート。省略すると自動で探す")
    parser.add_argument("--unity-port", type=int, default=None,
                        help=f"Unity が接続してくる TCP ポート（既定: {UNITY_PORT}。使えなければ {FALLBACK_PORT}。Unity も両方を試す）")
    parser.add_argument("--threshold-mg", type=float, default=300.0, help="異常とみなす振動（mg、既定: 300）")
    parser.add_argument("--warning-count", type=int, default=2, help="異常が何回続いたら注意にするか（既定: 2）")
    parser.add_argument("--limited-count", type=int, default=6, help="異常が何回続いたら制限にするか（既定: 6）")
    parser.add_argument("--send-interval", type=float, default=0.5, help="Unity に送る間隔（秒、既定: 0.5）")
    parser.add_argument("--log-dir", type=Path, default=REPO_ROOT / "Logs" / "sensor_bridge", help="記録の保存先")
    parser.add_argument("--verbose", action="store_true", help="測定値を 1 件ずつ表示する")
    parser.add_argument("--simulate", action="store_true",
                        help="StickS3 の代わりに、正常 → 注意 → 制限 → KEY2 で正常 の揺れを作って流す（段階の確かめ用）")
    parser.add_argument("--loop", action="store_true", help="--simulate を繰り返す")
    return parser.parse_args()


def say(message: str) -> None:
    print(f"{datetime.now():%H:%M:%S} {message}", flush=True)


def stop_on_terminate(signum, frame) -> None:
    raise KeyboardInterrupt


def main() -> None:
    args = parse_args()
    try:
        judge = Judge(JudgeConfig(args.threshold_mg, args.warning_count, args.limited_count))
    except ValueError as error:
        sys.exit(str(error))
    events: queue.Queue = queue.Queue()
    stop = threading.Event()
    try:
        server, args.unity_port = open_unity_server(args.unity_port, events, stop)
    except OSError as error:
        where = f"127.0.0.1:{args.unity_port}" if args.unity_port else f"127.0.0.1:{UNITY_PORT} と {FALLBACK_PORT}"
        sys.exit(f"{where} を使えない（{error.strerror or error}）。前に起動した中継か、中核システム（guardian_net）が動いていないか確かめる")
    signal.signal(signal.SIGTERM, stop_on_terminate)
    log = EventLog(args.log_dir)
    boot = f"sticks3-bridge-{datetime.now():%Y%m%d-%H%M%S}-{uuid.uuid4().hex[:4]}"
    threading.Thread(target=server.accept_forever, name="unity", daemon=True).start()
    if args.simulate:
        threading.Thread(target=simulate_serial, args=(events, stop, args.loop), name="simulate", daemon=True).start()
        say("段階の確かめ: StickS3 の代わりに作った揺れを流す（正常 → 注意 → 制限 → KEY2 で正常）")
    else:
        threading.Thread(target=read_serial, args=(args.serial, events, stop), name="serial", daemon=True).start()

    start = time.monotonic()
    log.write("start", start, robot=args.robot, boot=boot, threshold_mg=args.threshold_mg,
              warning_count=args.warning_count, limited_count=args.limited_count, unity_port=args.unity_port)
    say(f"中継を開始: ロボット {args.robot} / {args.threshold_mg:g} mg 以上が {args.warning_count} 回続いたら注意、"
        f"{args.limited_count} 回続いたら制限 / Unity 待ち受け 127.0.0.1:{args.unity_port}")
    say(f"記録: {log.path}")
    say("StickS3 のデータが届くまで Unity には何も送らない（届いたら正常で送り始める）")

    seq = 0
    next_send = start
    serial_missing_noted = False
    warmup_left = 0
    try:
        while True:
            try:
                kind, payload, at = events.get(timeout=0.05)
            except queue.Empty:
                kind = None
            if kind == "line":
                try:
                    message = json.loads(payload)
                except json.JSONDecodeError:
                    log.write("bad_line", at, line=payload[:200])
                    message = None
                if message and message.get("type") == "sample" and isinstance(message.get("vib_rms_g"), (int, float)):
                    rms_mg = message["vib_rms_g"] * 1000
                    warmup = warmup_left > 0
                    if warmup:
                        warmup_left -= 1
                        judge.note_alive(message.get("win_ms"), at)
                        raised = None
                    else:
                        raised = judge.on_sample(rms_mg, message.get("win_ms"), at)
                    log.write("sample", at, rms_mg=round(rms_mg, 2), peak_mg=round(message.get("vib_peak_g", 0) * 1000, 2),
                              win_ms=message.get("win_ms"), dev=message.get("dev"), dev_seq=message.get("seq"),
                              dev_t_ms=message.get("t_ms"), warmup=warmup)
                    if args.verbose:
                        say(f"振動 {rms_mg:6.1f} mg")
                    if raised:
                        log.write("level", at, level=judge.level, hold=judge.hold, reason=raised)
                        say(f"{args.robot}: {LEVEL_WORDS[judge.level]}（{raised}）" + ("※保持中" if judge.hold else ""))
                elif message and message.get("type") == "event" and message.get("event") == "button" and message.get("btn") == "KEY2":
                    accepted, reason = judge.on_key2(at)
                    log.write("key2", at, accepted=accepted, reason=reason)
                    say(f"KEY2: {reason}")
            elif kind == "sim_phase":
                log.write("sim_phase", at, phase=payload)
                say(f"[確かめ] {payload}")
            elif kind == "sim_cycle":
                say(f"[確かめ] 1 周終わり（いまの {args.robot}: {LEVEL_WORDS[judge.level]}）")
            elif kind == "serial_open":
                serial_missing_noted = False
                warmup_left = 0 if payload == "simulated" else WARMUP_SAMPLES
                log.write("serial_open", at, port=payload)
                say(f"StickS3 に接続: {payload}")
            elif kind == "serial_lost":
                log.write("serial_lost", at, detail=payload)
                say(f"StickS3 との接続が切れた: {payload}")
            elif kind == "serial_missing" and not serial_missing_noted:
                serial_missing_noted = True
                log.write("serial_missing", at)
                say("StickS3 が見つからない。USB をつなぐと自動で接続する")
            elif kind in ("unity_connected", "unity_disconnected"):
                log.write(kind, at, peer=payload)
                say("Unity が接続した" if kind == "unity_connected" else "Unity との接続が切れた")

            now = time.monotonic()
            held = judge.tick(now)
            if held:
                log.write("hold", now, reason=held)
                say(f"{args.robot}: 保持（{held}）")
            if now >= next_send:
                next_send = now + args.send_interval
                if server.client_count and judge.has_data:
                    seq += 1
                    server.send({"v": 1, "boot": boot, "seq": seq, "robots": [judge.robot_state(args.robot, now)]})
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        log.write("stop", time.monotonic())
        server.close()
        log.close()
        say("中継を終了")


if __name__ == "__main__":
    main()
