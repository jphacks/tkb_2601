#!/usr/bin/env python3
"""この PC で、ロボもり 中核がちゃんと動いているかを 1 回で確かめる（Windows / Mac）。中核を起動してから使う。

    python core/host/check_pc.py              （Windows は py -3 core\\host\\check_pc.py）
    python core/host/check_pc.py --no-door2   扉 2 の試し運転をしない
    python core/host/check_pc.py --no-drive-write   Drive に確認用のファイルを置かない

確かめること: 中核の画面と API、ほかの PC から届かないこと、中核が外へ出られないこと、扉 2 の出口が許可した宛先だけを
通すこと、扉 2 の試し運転（Gemini・Bluesky には送らない）、PSoC 6 が USB に見えるか、Google Drive の同期フォルダ。
Unity の本線（StickS3 → センサー中継 → Unity）には触らない。47612 にもつながない（つなぐと Unity の接続を押し出すため）。
"""
from __future__ import annotations

import argparse
import json
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from psoc6_key import COMPOSE, ROOT, drive_check, find_drive_folder, stop_egress_command  # noqa: E402

BASE = "http://127.0.0.1:8000"
CYPRESS_VID = 0x04B4    # KitProg3（PSoC 6）
ESPRESSIF_VID = 0x303A  # M5StickS3。名前を見るだけで、開かない
results: list[tuple[str, str, str]] = []


def report(mark: str, item: str, detail: str) -> None:
    results.append((mark, item, detail))
    print(f"{mark:<4} {item}: {detail}", flush=True)


KEY_FILE = ROOT / "Logs" / ".robomori-host-key"
HOST_KEY = KEY_FILE.read_text(encoding="ascii", errors="ignore").strip() if KEY_FILE.is_file() else ""


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def status(url: str) -> int:
    """HTTP の答えの番号だけ（転送はたどらない）。届かなければ 0。"""
    try:
        with urllib.request.build_opener(_NoRedirect).open(url, timeout=3.0) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code
    except OSError:
        return 0


def get(path: str, timeout: float = 3.0):
    with urllib.request.urlopen(BASE + path, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8")) if path.startswith("/api") else response.status


def compose(*args: str, timeout: float = 120) -> subprocess.CompletedProcess:
    return subprocess.run(["docker", "compose", "-f", str(COMPOSE), *args], cwd=ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout)


def lan_address() -> str | None:
    """この PC の LAN 側の住所（UDP の connect は何も送らない）。"""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.connect(("192.0.2.1", 9))
            address = probe.getsockname()[0]
        return None if address.startswith("127.") else address
    except OSError:
        return None


def check_core() -> bool:
    try:
        get("/healthz")
    except (OSError, ValueError) as error:
        report("NG", "中核の画面", f"{BASE} に届かない（{error}）。start.sh / start.ps1 で起動してから")
        return False
    report("OK", "中核の画面", f"{BASE} が答えた")
    locked = {"/": 302, "/api/state": 401, "/api/layout": 401, "/api/drive": 401, "/static/guardian/dashboard.js": 302}
    open_paths = [path for path, code in locked.items() if status(BASE + path) != code]
    if open_paths:
        report("NG", "合言葉", "合言葉なしで見えてしまう: " + ", ".join(open_paths))
    else:
        report("OK", "合言葉", "画面・データ・画面のスクリプトは、ワンタイムパスワードで入るまで見えない")
    if not HOST_KEY:
        report("注意", "起動ごとの鍵", "Logs/.robomori-host-key がない。start.sh / start.ps1 で起動すると、中核の状態まで確かめる")
        return True
    try:
        state = get("/api/key")
    except (OSError, ValueError) as error:
        report("NG", "起動ごとの鍵", f"中核が鍵を受け付けない（{error}）。start.sh / start.ps1 で起動し直す")
        return True
    started = time.perf_counter()
    for _ in range(10):
        get("/api/key")
    report("OK", "中核の速さ", f"/api/key 1 回 {(time.perf_counter() - started) * 100:.0f} ms（10 回の平均）")
    unity = state["unity"]
    if unity["connected"]:
        report("OK", "Unity（扉 1）", "代役（sim）とつながっている" if unity.get("stand_in") else "Unity とつながっている")
    else:
        report("注意", "Unity（扉 1）", "つながっていない。Unity を起動するか、start の --sim / -Sim で代役を使う")
    plan = state["schedule"]
    report("OK", "時間割", f"いま「{plan['label']}」。扉 2 は " + ("SW2 で開けられる" if plan["door2_allowed"] else "開かない"))
    relay = state["relay"]
    report("OK" if relay == "fresh" else "注意", "センサー中継の記録",
           {"fresh": "測定値が届いている", "stale": "途切れている", "none": "記録なし（中継が動いていない）"}[relay])
    return True


def check_lan() -> None:
    address = lan_address()
    if address is None:
        report("注意", "ほかの PC から", "LAN の住所が分からないので、確かめていない")
        return
    try:
        with socket.create_connection((address, 47612), timeout=1.0):
            hub = True
    except OSError:
        hub = False
    if hub:
        report("NG", "ほかの PC から", f"{address}:47612 に届いてしまう。docker-compose.yml の ports が 127.0.0.1 になっているか確かめる")
    else:
        report("OK", "ほかの PC から", f"{address} の 47612（Unity の口）には届かない")
    # 8000 は、start が出す host-only の中継（この PC からの接続だけを中核へ通す）が受けていてよい
    screen = status(f"http://{address}:8000/healthz")
    if screen == 200:
        report("OK", "画面の住所", f"http://{address}:8000 は host-only の中継（この PC からだけ。ほかの PC からはすぐ切る。合言葉も要る）")
    elif screen == 0:
        report("OK", "画面の住所", f"{address}:8000 には出していない（画面は http://127.0.0.1:8000）")
    else:
        report("注意", "画面の住所", f"{address}:8000 が思わぬ答え（{screen}）")


def check_core_offline() -> None:
    code = ("import socket\n"
            "for target in (('1.1.1.1', 443), ('example.com', 443)):\n"
            "    try:\n"
            "        socket.create_connection(target, 3).close(); print('reached', target[0])\n"
            "    except OSError:\n"
            "        pass\n")
    result = compose("exec", "-T", "web", "python", "-c", code, timeout=30)
    if result.returncode != 0:
        report("注意", "中核から外へ", f"確かめられなかった（{result.stderr.strip()[:120]}）")
    elif "reached" in result.stdout:
        report("NG", "中核から外へ", "web コンテナから外に届いてしまう: " + result.stdout.strip())
    else:
        report("OK", "中核から外へ", "web コンテナから 1.1.1.1 にも example.com にも届かない（名前も引けない）")


def check_egress() -> None:
    code = ("import urllib.request\n"
            "def go(url, proxy=True):\n"
            "    opener = urllib.request.build_opener() if proxy else urllib.request.build_opener(urllib.request.ProxyHandler({}))\n"
            "    try:\n"
            "        with opener.open(url, timeout=15) as r: return 'ok'\n"
            "    except urllib.error.HTTPError as e: return 'ok' if e.code < 500 and 'Tunnel' not in str(e) else 'ng'\n"
            "    except Exception as e: return 'refused' if '403' in str(e) else 'ng'\n"
            "print('allowed', go('https://bsky.social/xrpc/_health'))\n"
            "print('other', go('https://example.com/'))\n"
            "print('direct', go('https://bsky.social/xrpc/_health', proxy=False))\n")
    try:
        result = compose("run", "--rm", "--no-TTY", "--entrypoint", "python", "door2", "-c", code, timeout=120)
    finally:
        subprocess.run(stop_egress_command(), cwd=ROOT, capture_output=True)
    lines = dict(line.split(" ", 1) for line in result.stdout.splitlines() if " " in line)
    if lines.get("other") != "refused" or lines.get("direct") == "ok":
        report("NG", "扉 2 の出口", f"許可していない宛先に届いた可能性がある: {lines or result.stderr.strip()[:160]}")
    elif lines.get("allowed") == "ok":
        report("OK", "扉 2 の出口", "bsky.social には中継を通して届き、example.com は断り、中継を通さない接続は届かない")
    else:
        report("注意", "扉 2 の出口", "許可していない宛先は断った。bsky.social にも届かない（この PC がインターネットにつながっていないなら正常）")


def check_door2() -> None:
    try:
        result = compose("run", "--rm", "--no-TTY", "-e", "DOOR2_DRY_RUN=1", "-e", "OPENED_BY=check_pc.py の試し運転", "door2", timeout=180)
    finally:
        subprocess.run(stop_egress_command(), cwd=ROOT, capture_output=True)
    last = [line for line in result.stdout.splitlines() if line.strip()][-1:] or [result.stderr.strip()[-160:]]
    if result.returncode == 0:
        report("OK", "扉 2 の試し運転", last[0])
    elif result.returncode == 3:
        report("OK", "扉 2 の試し運転", "時間割で鍵がかかっていたので開けなかった（正しい動き）")
    else:
        report("NG", "扉 2 の試し運転", f"終了コード {result.returncode}: {last[0]}")


def check_psoc() -> None:
    try:
        from serial.tools import list_ports
    except ImportError:
        report("注意", "PSoC 6", "pyserial が入っていない（pip install pyserial）。PSoC 6 の鍵が使えない")
        return
    ports = list(list_ports.comports())
    psoc = [p.device for p in ports if p.vid == CYPRESS_VID]
    sticks = [p.device for p in ports if p.vid == ESPRESSIF_VID]
    note = f"（StickS3 は {', '.join(sticks)} に見えている。開かない）" if sticks else ""
    if psoc:
        report("OK", "PSoC 6", f"KitProg3 が {', '.join(psoc)} に見えている{note}。SW2 とランプは psoc6_check.py で確かめる")
    else:
        report("注意", "PSoC 6", f"KitProg3 が見えない。KitProg3 側の USB をつなぐ{note}")


def check_drive(write: bool) -> None:
    folder = find_drive_folder()
    if folder is None:
        report("注意", "Google Drive", "Google Drive for desktop が見つからない。入れて店のアカウントでログインする")
        return
    if not write:
        report("OK", "Google Drive", f"同期フォルダ: {folder}")
        return
    try:
        path = drive_check(folder)
    except OSError as error:
        report("NG", "Google Drive", f"{folder} に書けない（{error}）")
        return
    report("OK", "Google Drive", f"{path} を置いた。drive.google.com の robomori フォルダに出れば届いている")


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description="この PC でロボもり 中核が動いているかを確かめる")
    parser.add_argument("--no-door2", action="store_true", help="扉 2 の出口と試し運転を確かめない")
    parser.add_argument("--no-drive-write", action="store_true", help="Google Drive に確認用のファイルを置かない")
    args = parser.parse_args()
    if subprocess.run(["docker", "compose", "version"], capture_output=True).returncode != 0:
        report("NG", "Docker", "docker compose が動かない。Docker Desktop を起動する")
        return 1
    report("OK", "Docker", "docker compose が動く")
    if check_core():
        check_lan()
        check_core_offline()
        if not args.no_door2:
            check_egress()
            check_door2()
    check_psoc()
    check_drive(not args.no_drive_write)
    bad = [r for r in results if r[0] == "NG"]
    print()
    print(f"NG {len(bad)} 件 / 注意 {sum(r[0] == '注意' for r in results)} 件 / OK {sum(r[0] == 'OK' for r in results)} 件")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
