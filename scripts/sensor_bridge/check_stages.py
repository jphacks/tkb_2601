"""ロボットの段階が 3 つ（正常・注意・制限）に切り替わり、KEY2 で正常に戻るかを、Unity なしで確かめる。

中継を --simulate で空いているポートに起動し、Unity の代わりに接続して、届いた段階の移り変わりを調べる。
StickS3 も Unity も要らない（StickS3 本体には何もしない）。
実行: python scripts/sensor_bridge/check_stages.py      合格なら終了コード 0
"""
import json
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
EXPECTED = ["Normal", "Warning", "Limited", "Normal"]
WORDS = {"Normal": "正常", "Warning": "注意", "Limited": "制限", "Hold": "保持"}


def free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main() -> int:
    port = free_port()
    with tempfile.TemporaryDirectory() as logs:
        relay = subprocess.Popen([sys.executable, str(HERE / "sensor_bridge.py"), "--simulate", "--unity-port", str(port),
                                  "--log-dir", logs], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        seen, holds = [], 0
        try:
            deadline = time.monotonic() + 30
            sock = None
            while sock is None and time.monotonic() < deadline:
                try:
                    sock = socket.create_connection(("127.0.0.1", port), timeout=1)
                except OSError:
                    if relay.poll() is not None:
                        print("NG 中継が起動しなかった:", relay.stderr.read().decode("utf-8", "replace")[-400:])
                        return 1
                    time.sleep(0.2)
            if sock is None:
                print("NG 中継につながらなかった")
                return 1
            sock.settimeout(3)
            with sock, sock.makefile(encoding="utf-8") as lines:
                while time.monotonic() < deadline and seen != EXPECTED:
                    line = lines.readline()
                    if not line:
                        break
                    robot = json.loads(line)["robots"][0]
                    holds += robot["final"] == "Hold" and robot["capability"] != "Limited"
                    if not seen or seen[-1] != robot["capability"]:
                        seen.append(robot["capability"])
                        print(f"  {time.strftime('%H:%M:%S')} ロボット {robot['id']}: {WORDS[robot['capability']]}（{robot['reasons'][0]}）", flush=True)
        except (OSError, ValueError, KeyError) as error:
            print("NG 受け取りに失敗:", error)
            return 1
        finally:
            relay.terminate()
            try:
                relay.wait(timeout=5)
            except subprocess.TimeoutExpired:
                relay.kill()
                relay.wait(timeout=5)
            finally:
                relay.stderr.close()
    if seen == EXPECTED and holds == 0:
        print("OK 正常 → 注意 → 制限 → (KEY2) → 正常 の 3 段階が切り替わった")
        return 0
    print("NG 期待:", " → ".join(WORDS[s] for s in EXPECTED), "／実際:", " → ".join(WORDS.get(s, s) for s in seen) or "なし",
          f"（保持 {holds} 回）" if holds else "")
    return 1


if __name__ == "__main__":
    sys.exit(main())
