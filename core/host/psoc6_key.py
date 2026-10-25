#!/usr/bin/env python3
"""PSoC 6 を扉 2 の鍵にする（PC 側で動かす。USB の機器は Docker に渡せないため）。

- SW2 が押されたら `docker compose run --rm door2` を 1 回だけ動かす。扉 2 のコンテナは、そのときだけ存在する。
- ランプ: 点灯 = 扉 2 が開いている / 速い点滅 = 失敗 / ゆっくり点滅 = 外へ出す候補あり / 消灯 = なし
  （状態は中核の画面と同じ http://127.0.0.1:8000/api/state から読む）
- 時間割で扉 2 に鍵がかかっている間（単独・店内）は、SW2 を押しても開けない（速い点滅で知らせる）。
- door2 が終わったら、扉 2 の出口（egress の中継）もすぐ消す（中継は 30 秒使われなければ自分でも終わる）。
- --drive-folder を付けると、書き出しフォルダ（Logs/drive_out）に置かれた要約を Google Drive の同期フォルダへ写す。
  --drive-folder auto なら、Google Drive for desktop の「マイドライブ」を探し、その中の robomori フォルダに写す。
  コンテナは Google の鍵を持たず、Drive に送るのは PC の Drive for desktop。写すのは決まった名前の普通のファイルだけ
  （リンクは辿らない。コンテナがリンクを置いても、PC のほかのファイルが Drive に出ることはない）。
- M5StickS3 は開かない。PSoC 6 とのやり取りは StickS3 と同じ形（firmware/psoc6_share_panel/README.md）。
- 中核の状態は /api/key から、起動ごとの鍵（--host-key。start.sh / start.ps1 が作り Logs/.robomori-host-key に置く）を付けて読む。
- --listen にこの PC の IPv4 を渡すと、その住所で画面を出す（HostOnlyRelay）。この PC からの接続だけを 127.0.0.1:8000 へ通す。
- Drive へ写すたびに、書き出しフォルダの .drive-log.jsonl に結果を 1 行書く（画面の「Google Drive の記録」が読む）。

使い方: python core/host/psoc6_key.py            （pyserial が要る。センサー中継と同じ環境でよい）
        python core/host/psoc6_key.py --keyboard （PSoC 6 がないとき。Enter で扉 2 を開ける）
        python core/host/psoc6_key.py --drive-folder auto   （Google Drive for desktop を探して写す）
        python core/host/psoc6_key.py --drive-folder auto --drive-check   （Drive に届くか試して終わる）
"""
from __future__ import annotations

import argparse
import ipaddress
import json
import os
import platform
import queue
import re
import shutil
import socket
import stat
import struct
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import webbrowser
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
COMPOSE = ROOT / "core" / "docker-compose.yml"
sys.path.insert(0, str(ROOT / "scripts" / "guardian_share"))
EXPORT_FOLDER = ROOT / "Logs" / "drive_out"
LAMP_INTERVAL = 0.2
STATE_INTERVAL = 0.5
COPY_INTERVAL = 2.0
FLASH_SECONDS = 2.0
REFUSED = 3                                   # door2 が時間割で開けなかったときの終了コード
DRIVE_SUBFOLDER = "robomori"
DRIVE_ROOTS = ("マイドライブ", "My Drive")
EXPORT_NAME = re.compile(r"guardian-records-\d{8}-\d{6}\.(md|jsonl)")
EXPORT_LIMIT = 5 * 1024 * 1024
KEY_FILE = ROOT / "Logs" / ".robomori-host-key"
DRIVE_LOG = ".drive-log.jsonl"
_copy_problems: dict[str, str] = {}


def write_drive_log(folder: Path, **entry) -> None:
    """画面の「Google Drive の記録」が読む 1 行（書けなくても写す処理は止めない）。"""
    if not folder.is_dir():
        return
    entry["at"] = datetime.now().isoformat(timespec="seconds")
    try:
        with open(folder / DRIVE_LOG, "a", encoding="utf-8") as file:
            file.write(json.dumps(entry, ensure_ascii=False) + "\n")
    except OSError:
        pass


def say(message: str) -> None:
    print(f"{datetime.now():%H:%M:%S} {message}", flush=True)


def lamp_on(state: dict | None, running: bool, flash_until: float, now: float) -> bool:
    """点灯 = 扉 2 が開いている / 速い点滅 = 失敗 / ゆっくり点滅 = 外へ出す候補あり / 消灯 = なし（中核に届かないときも）"""
    if running or (state or {}).get("door2", {}).get("state") == "external":
        return True
    if now < flash_until:
        return int(now / 0.25) % 2 == 0
    if (state or {}).get("door2", {}).get("pending"):
        return int(now / 0.5) % 2 == 0
    return False


def copy_new_exports(source: Path, target: Path, done: set[str]) -> list[str]:
    """書き出しフォルダの新しい要約を、Drive の同期フォルダへ写す。写した名前を返す。

    写すのは決まった名前の普通のファイルだけ。リンクや大きすぎるものは写さない。書きかけが Drive に出ないよう、
    いったん別名で書いてから置き換える。
    """
    copied = []
    try:
        entries = sorted(os.scandir(source), key=lambda e: e.name)
    except OSError:
        return copied
    for entry in entries:
        if entry.name in done or not EXPORT_NAME.fullmatch(entry.name):
            continue
        try:
            info = entry.stat(follow_symlinks=False)
        except OSError:
            continue
        if not stat.S_ISREG(info.st_mode) or info.st_size > EXPORT_LIMIT:
            done.add(entry.name)
            continue
        destination = target / entry.name
        if destination.exists() and destination.stat().st_size == info.st_size:
            done.add(entry.name)
            continue
        partial = target / ("." + entry.name + ".part")
        try:
            flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0)
            fd = os.open(entry.path, flags)
            with os.fdopen(fd, "rb") as reader:
                opened = os.fstat(reader.fileno())   # 調べてから開くまでに差し替えられていないか
                if not stat.S_ISREG(opened.st_mode) or opened.st_size > EXPORT_LIMIT:
                    done.add(entry.name)
                    continue
                with open(partial, "wb") as writer:
                    writer.write(reader.read(EXPORT_LIMIT))
            os.replace(partial, destination)
        except OSError as error:
            # 同期ソフトが掴んでいるときなど。次の回に試し直す（同じ理由は 1 回だけ記録する）
            if _copy_problems.get(entry.name) != str(error):
                _copy_problems[entry.name] = str(error)
                write_drive_log(source, kind="copy", file=entry.name, ok=False, to=str(target), error=str(error)[:200])
            continue
        done.add(entry.name)
        copied.append(entry.name)
        _copy_problems.pop(entry.name, None)
        write_drive_log(source, kind="copy", file=entry.name, ok=True, to=str(target))
    return copied


def drive_candidates(home: Path, system: str) -> list[str]:
    """Google Drive for desktop の「マイドライブ」がありそうな場所（探す順）。"""
    found: list[str] = []
    if system == "Windows":
        for letter in "GHIJKLMNOPQRSTUVWXYZDEF":           # ストリーミング（既定）は G: などのドライブになる
            found += [f"{letter}:\\{name}" for name in DRIVE_ROOTS]
        found += [str(home / name) for name in DRIVE_ROOTS]   # ミラーリングのとき
    elif system == "Darwin":
        for root in sorted((home / "Library" / "CloudStorage").glob("GoogleDrive-*")):
            found += [str(root / name) for name in DRIVE_ROOTS]
    return found


def find_drive_folder(home: Path | None = None, system: str | None = None) -> Path | None:
    """見つかったマイドライブの中の robomori フォルダ（なければ作る）。見つからなければ None。"""
    for candidate in drive_candidates(home or Path.home(), system or platform.system()):
        root = Path(candidate)
        try:
            if root.is_dir():
                folder = root / DRIVE_SUBFOLDER
                folder.mkdir(exist_ok=True)
                return folder
        except OSError:
            continue
    return None


def resolve_drive_folder(value: str | None) -> tuple[Path | None, str]:
    """--drive-folder の値から写す先を決める。(写す先, 知らせる文)。写さないときは写す先が None。"""
    if not value:
        return None, ""
    if value == "auto":
        folder = find_drive_folder()
        if folder is None:
            return None, "Google Drive for desktop が見つからない。入れてログインすると、要約を Drive に写せる（いまは写さない）"
        return folder, ""
    folder = Path(value)
    try:
        if not folder.is_dir() and folder.parent.is_dir():
            folder.mkdir()
    except OSError:
        pass
    if not folder.is_dir():
        return None, f"Drive の同期フォルダが見つからない: {folder}"
    return folder, ""


def drive_check(folder: Path) -> Path:
    """Drive の同期フォルダに小さなファイルを置く。Drive の画面（drive.google.com）に出れば、つながっている。"""
    path = folder / f"robomori-drive-check-{datetime.now():%Y%m%d-%H%M%S}.txt"
    partial = folder / ("." + path.name + ".part")
    partial.write_text(f"ロボもり: Google Drive へ写せるかの確認（{datetime.now():%Y-%m-%d %H:%M:%S}）\n", encoding="utf-8")
    os.replace(partial, path)
    return path


def door2_locked(state: dict | None) -> str | None:
    """時間割で扉 2 に鍵がかかっていれば、その時間割の名前。開けてよければ None（中核に届かないときは door2 が決める）。"""
    plan = (state or {}).get("schedule") or {}
    if plan and plan.get("door2_allowed") is False:
        return plan.get("label") or "鍵"
    return None


def stop_egress_command() -> list[str]:
    return ["docker", "compose", "-f", str(COMPOSE), "--profile", "door2", "rm", "--stop", "--force", "egress"]


def door2_command(source: str, dry_run: bool) -> list[str]:
    command = ["docker", "compose", "-f", str(COMPOSE), "run", "--rm", "-e", f"OPENED_BY={source}"]
    if dry_run:
        command += ["-e", "DOOR2_DRY_RUN=1"]
    return command + ["door2"]


def read_state(url: str, key: str = "") -> tuple[dict | None, str]:
    """中核の状態と、読めなかった理由（"" / "key" = 鍵が合わない / "down" = 届かない）。"""
    request = urllib.request.Request(url, headers={"X-Robomori-Key": key} if key else {})
    try:
        with urllib.request.urlopen(request, timeout=1.0) as response:
            return json.loads(response.read().decode("utf-8")), ""
    except urllib.error.HTTPError as error:
        return None, "key" if error.code == 403 else "down"
    except (OSError, ValueError):
        return None, "down"


def host_ipv4() -> str:
    """この PC が外へ出るときに使う IPv4（UDP の connect は何も送らない）。なければ空。"""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.connect(("192.0.2.1", 9))
            address = probe.getsockname()[0]
    except OSError:
        return ""
    return "" if address.startswith("127.") else address


class HostOnlyRelay:
    """この PC の IPv4 で画面を出す。この PC 自身からの接続だけを 127.0.0.1 の門番へ通し、ほかの PC からの接続は
    何も読まずにすぐ切る（core/host/HostOnlyRelay.cs と同じ決まり）。運ぶ中身は読まない（合言葉は中核が確かめる）。"""

    MAX_OPEN = 64
    OWN_REFRESH = 10.0

    def __init__(self, address: str, port: int, upstream_port: int = 8000, allow=None):
        if ipaddress.ip_address(address).version != 4:
            raise ValueError("この PC の IPv4 が要る: " + address)
        self.server = socket.create_server((address, port), backlog=32)
        self.upstream_port = upstream_port
        self.allow = allow or self.is_own
        self.served = self.refused = self.open = 0
        self.last_refused = ""
        self._lock = threading.Lock()
        self._own: set[str] = set()
        self._own_at = 0.0
        self._running = True
        threading.Thread(target=self._accept, name="host-only relay", daemon=True).start()

    def stop(self) -> None:
        self._running = False
        self.server.close()

    def is_own(self, host: str) -> bool:
        address = ipaddress.ip_address(host)
        if address.is_loopback:
            return True
        with self._lock:
            if time.monotonic() - self._own_at > self.OWN_REFRESH:
                own = {host_ipv4()}
                try:
                    own |= {info[4][0] for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)}
                except OSError:
                    pass
                self._own, self._own_at = own - {""}, time.monotonic()
            return host in self._own

    def _accept(self) -> None:
        while self._running:
            try:
                client, (host, _) = self.server.accept()
            except OSError:
                return
            try:
                allowed = self.allow(host)
            except ValueError:
                allowed = False
            if not allowed or self.open >= self.MAX_OPEN:
                with self._lock:
                    self.refused += 1
                    self.last_refused = host
                try:
                    client.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
                except OSError:
                    pass
                client.close()   # 何も読まずに切る
                continue
            threading.Thread(target=self._serve, args=(client,), daemon=True).start()

    def _serve(self, client: socket.socket) -> None:
        with self._lock:
            self.open += 1
        upstream = None
        try:
            upstream = socket.create_connection(("127.0.0.1", self.upstream_port), timeout=5)
            upstream.settimeout(None)
            with self._lock:
                self.served += 1
            back = threading.Thread(target=self._pump, args=(upstream, client), daemon=True)
            back.start()
            self._pump(client, upstream)
            back.join(5)
        except OSError:
            pass
        finally:
            for side in (client, upstream):
                if side is not None:
                    side.close()
            with self._lock:
                self.open -= 1

    @staticmethod
    def _pump(source: socket.socket, target: socket.socket) -> None:
        try:
            while True:
                data = source.recv(16384)
                if not data:
                    break
                target.sendall(data)
        except OSError:
            pass
        for side in (source, target):   # どちらかが終わったら両方を閉じる
            try:
                side.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass


SIGN_IN = re.compile(r"/login#code=[A-Z2-9]{4}-[A-Z2-9]{4}")


def open_screen(base: str, sign_in: str, opener=webbrowser.open) -> bool:
    """画面を出せた住所を Logs/.robomori-screen に書き、頼まれていればブラウザで開いて自動で入る。
    合言葉は # の後ろなので、サーバーには送られない。形の決まった /login#code=… だけを開く。"""
    try:
        (ROOT / "Logs" / ".robomori-screen").write_text(base, encoding="ascii")
    except OSError:
        pass
    if not SIGN_IN.fullmatch(sign_in or ""):
        return False
    try:
        opened = bool(opener(base + sign_in))
    except Exception:  # ブラウザがない環境でも、鍵は動かし続ける
        opened = False
    say(f"ブラウザで画面を開いた（{base}）。ワンタイムパスワードで自動で入る" if opened else f"ブラウザを開けなかった。{base} を開いて、ワンタイムパスワードを入れる")
    return opened


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description="PSoC 6 の SW2 で扉 2 を開ける（PC 側）")
    parser.add_argument("--port", help="PSoC 6 のシリアルポート（省略すると KitProg3 を自動で探す）")
    parser.add_argument("--keyboard", action="store_true", help="Enter キーでも扉 2 を開ける（PSoC 6 がないときのリハーサル）")
    parser.add_argument("--no-psoc", action="store_true", help="PSoC 6 を使わない（--keyboard と一緒に使う）")
    parser.add_argument("--dry-run", action="store_true", help="試し運転。扉 2 を開けても Gemini と Bluesky には送らない")
    parser.add_argument("--state-url", default="http://127.0.0.1:8000/api/key", help="中核の状態（既定: http://127.0.0.1:8000/api/key）")
    parser.add_argument("--host-key", default="", help="起動ごとの鍵（省略すると Logs/.robomori-host-key を読む）")
    parser.add_argument("--listen", default="", help="この PC の IPv4。その住所で画面を出す（この PC からの接続だけを通す）")
    parser.add_argument("--listen-port", type=int, default=8000, help="--listen の port（既定: 8000）")
    parser.add_argument("--open-screen", default="", help="'/login#code=XXXX-XXXX' なら、画面を出せたあとでブラウザを開き、自動で入る")
    parser.add_argument("--drive-folder", help="Google Drive for desktop の同期フォルダ。auto なら探す。書き出された要約をここへ写す")
    parser.add_argument("--drive-check", action="store_true", help="Drive の同期フォルダに確認用のファイルを置き、たまった要約を写して終わる")
    parser.add_argument("--export-folder", type=Path, default=EXPORT_FOLDER, help="コンテナが要約を置くフォルダ（core/.env の EXPORT_FOLDER と同じ）")
    args = parser.parse_args()
    requested = args.drive_folder
    args.drive_folder, note = resolve_drive_folder(args.drive_folder)
    if note:
        say(note)
    if requested and requested != "off":
        write_drive_log(args.export_folder, kind="drive", ok=args.drive_folder is not None, to=str(args.drive_folder or ""),
                        **({} if args.drive_folder else {"error": "Google Drive for desktop の同期フォルダが見つからない"}))
    if not args.host_key and KEY_FILE.is_file():
        args.host_key = KEY_FILE.read_text(encoding="ascii", errors="ignore").strip()
    if args.drive_check:
        if args.drive_folder is None:
            say("Drive の同期フォルダが決まらない（--drive-folder auto か、フォルダを指定する）")
            return 1
        path = drive_check(args.drive_folder)
        copied = copy_new_exports(args.export_folder, args.drive_folder, set())
        say(f"確認用のファイルを置いた: {path}")
        say(f"たまっていた要約を {len(copied)} 件写した（{args.export_folder} → {args.drive_folder}）")
        say(f"Google Drive（drive.google.com）の「{DRIVE_SUBFOLDER}」フォルダに {path.name} が出れば、つながっている")
        return 0
    if shutil.which("docker") is None:
        say("docker が見つからない。Docker Desktop を起動してから、もう一度")
        return 1

    events: queue.Queue = queue.Queue()
    stop = threading.Event()
    psoc = None
    if not args.no_psoc:
        try:
            import serial  # noqa: F401  ここで確かめる（share_bridge は読むスレッドの中で初めて serial を使う）
            from share_bridge import Psoc6Link   # KitProg3 を探して開く（exclusive。StickS3 は開かない）
        except ImportError:
            say("pyserial が入っていない（pip install pyserial）。--keyboard で Enter を鍵にできる")
            return 1
        psoc = Psoc6Link(args.port, events, stop)
        threading.Thread(target=psoc.run, name="psoc6", daemon=True).start()
    if args.keyboard:
        def keyboard():
            for _ in sys.stdin:
                events.put(("key", None))
        threading.Thread(target=keyboard, name="keyboard", daemon=True).start()

    door = {"running": False, "flash_until": 0.0}
    state: dict | None = None
    reachable = None

    def open_door2(source: str) -> None:
        if door["running"] or (state or {}).get("door2", {}).get("state") == "external":
            say("扉 2 はもう開いている")
            return
        locked = door2_locked(state)
        if locked:
            door["flash_until"] = time.monotonic() + FLASH_SECONDS
            say(f"時間割が「{locked}」のあいだは、扉 2 を開けない")
            return
        door["running"] = True
        say(f"扉 2 を開ける（{source}）。door2 コンテナを立ち上げる")

        def work():
            result = subprocess.run(door2_command(source, args.dry_run), cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
            for line in (result.stdout + result.stderr).splitlines():
                if line.strip() and "Container" not in line and "Network" not in line:
                    say("  " + line.strip())
            # 扉 2 の出口も、すぐ消す
            subprocess.run(stop_egress_command(), cwd=ROOT, capture_output=True)
            events.put(("door2_done", result.returncode))

        threading.Thread(target=work, name="door2", daemon=True).start()

    say(f"PSoC 6 を扉 2 の鍵として待つ（中核: {args.state_url}）。終わるときは Ctrl+C")
    if args.drive_folder:
        say(f"書き出された要約を Google Drive へ写す: {args.export_folder} → {args.drive_folder}")
    if not args.host_key:
        say("起動ごとの鍵がない。core/start.sh から起動すると、中核の状態を読める")
    relay = None
    if args.listen:
        try:
            relay = HostOnlyRelay(args.listen, args.listen_port)
            say(f"画面: http://{args.listen}:{args.listen_port}（この PC からだけ開ける。ほかの PC からの接続はすぐ切る）")
        except (OSError, ValueError) as error:
            say(f"{args.listen}:{args.listen_port} で画面を出せなかった（{error}）。画面は http://127.0.0.1:8000 で開く")
    open_screen(f"http://{args.listen}:{args.listen_port}" if relay is not None else "http://127.0.0.1:8000", args.open_screen)
    refused_seen = 0
    copied_names: set[str] = set()
    next_lamp = next_state = next_copy = time.monotonic()
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
                if isinstance(message, dict) and message.get("type") == "event" and message.get("event") == "button":
                    open_door2("人が PSoC 6 の SW2 を押した")
            elif kind == "key":
                open_door2("人が Enter キーを押した")
            elif kind == "door2_done":
                door["running"] = False
                if payload == 0:
                    say("扉 2 を閉じた（door2 コンテナと出口の中継は消えた）")
                elif payload == REFUSED:
                    door["flash_until"] = now + FLASH_SECONDS
                    say("時間割で鍵がかかっていたので、扉 2 は開けなかった")
                else:
                    door["flash_until"] = now + FLASH_SECONDS
                    say(f"扉 2 のやりとりで、うまくいかないことがあった（終了コード {payload}）。扉 2 は閉じた")
            elif kind == "psoc_open":
                say(f"PSoC 6 に接続: {payload}")
            elif kind == "psoc_lost":
                say(f"PSoC 6 との接続が切れた: {payload}")
            elif kind == "psoc_missing":
                say("PSoC 6 が見つからない。KitProg3 側の USB をつなぐと自動で接続する")
            if now >= next_state:
                next_state = now + STATE_INTERVAL
                state, problem = read_state(args.state_url, args.host_key)
                if (state is not None) != reachable:
                    reachable = state is not None
                    say("中核に届いた" if reachable else "中核に届いたが、起動ごとの鍵が合わない（core/start.sh で起動し直す）"
                        if problem == "key" else "中核に届かない（core/start.sh で起動する）")
                if relay is not None and relay.refused != refused_seen:
                    refused_seen = relay.refused
                    say(f"ほかの PC（{relay.last_refused}）から画面につなごうとしたので、切った（累計 {refused_seen} 回）")
            if args.drive_folder and now >= next_copy:
                next_copy = now + COPY_INTERVAL
                for name in copy_new_exports(args.export_folder, args.drive_folder, copied_names):
                    say(f"Google Drive の同期フォルダへ写した: {name}")
            if psoc is not None and now >= next_lamp:
                next_lamp = now + LAMP_INTERVAL
                psoc.send({"cmd": "lamp", "on": lamp_on(state, door["running"], door["flash_until"], now)})
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        if relay is not None:
            relay.stop()
        if psoc is not None:
            psoc.send({"cmd": "lamp", "on": False})
        say("終了")
    return 0


if __name__ == "__main__":
    sys.exit(main())
