"""Unity を起動せずに中継を確かめるための受信プログラム。

GuardianNetworkBridge と同じく 127.0.0.1 の中継に接続し、届いた状態を 1 行ずつ表示する。
実行: python scripts/sensor_bridge/fake_unity.py [秒数] [--port 47610]
"""
import argparse
import json
import socket
import time


def main() -> None:
    parser = argparse.ArgumentParser(description="中継から届く状態を表示する（Unity の代わり）")
    parser.add_argument("seconds", nargs="?", type=float, default=10, help="表示を続ける秒数（既定: 10）")
    parser.add_argument("--port", type=int, default=47610, help="中継のポート（既定: 47610）")
    args = parser.parse_args()

    end = time.monotonic() + args.seconds
    while time.monotonic() < end:
        try:
            with socket.create_connection(("127.0.0.1", args.port), timeout=1) as sock, sock.makefile(encoding="utf-8") as lines:
                sock.settimeout(2)
                while time.monotonic() < end:
                    line = lines.readline()
                    if not line:
                        break
                    packet = json.loads(line)
                    for robot in packet["robots"]:
                        print(f"seq={packet['seq']} {robot['id']} final={robot['final']} capability={robot['capability']} "
                              f"trust={robot['trust']} 理由={'／'.join(robot['reasons'])}", flush=True)
        except OSError:
            time.sleep(0.5)  # 中継がまだ起動していなければ待ってつなぎ直す


if __name__ == "__main__":
    main()
