#!/usr/bin/env python3
"""PSoC 6（CY8CPROTO-062-4343W）を USB に挿して、PC と行き来できるかを確かめる。Windows / macOS / Linux。

1. USB のシリアルポートを並べ、PSoC 6（KitProg3）を探す。M5StickS3 は開かない（中身を書き換えないため、触らない）
2. PSoC 6 を開き、hello を頼み、ランプを 0.5 秒ごとに点けたり消したりする（PC → PSoC 6）
3. 届いた行を表示し、StickS3 と同じ形（type・dev・seq・t_ms）かを確かめる（PSoC 6 → PC）
4. SW2 を押すと、そのイベントが届くかを確かめる
使い方: firmware/psoc6_share_panel/README.md の「つないで確かめる」
"""
from __future__ import annotations

import argparse
import json
import sys
import time

CYPRESS_VID = 0x04B4   # KitProg3（PSoC 6 キットの書き込み器と USB シリアル）
ESPRESSIF_VID = 0x303A  # M5StickS3（ESP32-S3）。このプログラムでは開かない
MICROPYTHON_SIGNS = (">>>", "MicroPython", "Type \"help()\"", "Traceback")


def check_line(message: dict, last_seq: int | None) -> list[str]:
    """main の形（StickS3 と同じ）からずれているところを返す。空なら合っている。"""
    problems = []
    if message.get("type") not in ("event", "sample"):
        problems.append(f"type が event / sample ではない（{message.get('type')!r}）")
    if not isinstance(message.get("dev"), str) or not message.get("dev"):
        problems.append("dev（どの機器か）がない")
    seq = message.get("seq")
    if isinstance(seq, bool) or not isinstance(seq, int) or seq < 0:
        problems.append("seq（通し番号）がない")
    elif last_seq is not None and seq != last_seq + 1:
        problems.append(f"seq が飛んだ（{last_seq} の次が {seq}）。途中の行が届かなかったか、PSoC 6 が再起動した")
    t_ms = message.get("t_ms")
    if isinstance(t_ms, bool) or not isinstance(t_ms, int) or t_ms < 0:
        problems.append("t_ms（起動からの時間）がない")
    if message.get("type") == "event" and not isinstance(message.get("event"), str):
        problems.append("event（何が起きたか）がない")
    if message.get("event") == "button" and not message.get("btn"):
        problems.append("btn（どのボタンか）がない")
    return problems


def describe_ports():
    from serial.tools import list_ports

    ports = sorted(list_ports.comports(), key=lambda p: p.device)
    print("USB のシリアルポート:")
    if not ports:
        print("  （ひとつもない）")
    for port in ports:
        vid = f"{port.vid:04X}:{port.pid:04X}" if port.vid is not None else "----:----"
        mark = "  ← PSoC 6（KitProg3）" if port.vid == CYPRESS_VID else "  ← M5StickS3（開かない）" if port.vid == ESPRESSIF_VID else ""
        print(f"  {port.device:<24} {vid}  {port.description or ''}{mark}")
    return ports


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description="PSoC 6 と PC が行き来できるかを確かめる")
    parser.add_argument("--port", help="PSoC 6 のポート（省略すると KitProg3 を自動で探す）")
    parser.add_argument("--seconds", type=float, default=30.0, help="確かめる時間（秒、既定: 30）。SW2 と hello が届けば早く終わる")
    parser.add_argument("--list", action="store_true", help="ポートを並べるだけで終わる")
    args = parser.parse_args()
    try:
        import serial
    except ImportError:
        print("pyserial が入っていない。pip install pyserial を実行してから、もう一度動かす")
        return 2

    ports = describe_ports()
    if args.list:
        return 0
    name = args.port
    if name is None:
        found = [p.device for p in ports if p.vid == CYPRESS_VID]
        if not found:
            print("\nPSoC 6 が見つからない。確かめること:")
            print("  - キットの KitProg3 側の USB 端子（基板の端の、書き込み器がある側）に挿したか")
            print("  - ケーブルがデータ通信に対応しているか（充電専用ではつながらない）")
            print("  - Windows: デバイスマネージャーの「ポート (COM と LPT)」に KitProg3 USB-UART が出るか")
            print("  - macOS: ターミナルで ls /dev/cu.usbmodem* に出るか")
            return 1
        name = found[0]
        if len(found) > 1:
            print(f"\nPSoC 6 が {len(found)} 台ある。{name} を使う（ほかは --port で選ぶ）")
    elif any(p.device == name and p.vid == ESPRESSIF_VID for p in ports):
        print(f"\n{name} は M5StickS3。このプログラムでは開かない")
        return 1

    print(f"\n{name} を開く（115200 bps）")
    try:
        device = serial.Serial(name, 115200, timeout=0.1, exclusive=True)
    except (serial.SerialException, OSError, ValueError) as error:
        print(f"開けなかった: {error}")
        print("共有中継・監視ハブ・Thonny・mpremote・ModusToolbox などが、このポートを使っていないか確かめる")
        return 1

    print("ランプを 0.5 秒ごとに点けたり消したりする。PSoC 6 の LED が点滅するか見ていてください。")
    print(f"SW2（USER ボタン）を 1 回押してください。最長 {args.seconds:g} 秒待つ。Ctrl+C で止める。\n")
    hello = button = False
    lamp_count = 0
    last_seq = None
    shapes_ok = 0
    problems_seen: list[str] = []
    micropython_only = False
    buffer = b""
    start = time.monotonic()
    next_lamp = start
    device.write(b'{"cmd":"hello"}\n')
    try:
        while time.monotonic() - start < args.seconds and not (hello and button):
            now = time.monotonic()
            if now >= next_lamp:
                next_lamp = now + 0.5
                lamp_count += 1
                device.write((json.dumps({"cmd": "lamp", "on": lamp_count % 2 == 1}) + "\n").encode("ascii"))
            buffer += device.read(256)
            while b"\n" in buffer:
                raw, buffer = buffer.split(b"\n", 1)
                text = raw.decode("utf-8", errors="replace").strip()
                if not text:
                    continue
                if any(sign in text for sign in MICROPYTHON_SIGNS) and not text.startswith("{"):
                    micropython_only = True
                    print(f"  （MicroPython）{text}")
                    continue
                try:
                    message = json.loads(text)
                except ValueError:
                    print(f"  JSON ではない行: {text[:80]}")
                    continue
                if not isinstance(message, dict):
                    print(f"  JSON のオブジェクトではない: {text[:80]}")
                    continue
                problems = check_line(message, last_seq)
                if isinstance(message.get("seq"), int):
                    last_seq = message["seq"]
                print(f"  受信 {text}" + ("" if not problems else "\n    ⚠ " + "\n    ⚠ ".join(problems)))
                problems_seen += problems
                shapes_ok += not problems
                if message.get("event") == "hello":
                    hello = True
                if message.get("event") == "button":
                    button = True
                    print(f"    → {message.get('btn')} の押下が届いた")
    except KeyboardInterrupt:
        pass
    finally:
        device.write(b'{"cmd":"lamp","on":false}\n')
        device.close()

    shape = "OK" if shapes_ok and not problems_seen else "NG" if problems_seen else "--"
    print("\n結果")
    print(f"  OK  USB で見えた（{name}）")
    print(f"  {'OK' if hello else 'NG'}  PSoC 6 → PC: hello が届いた")
    print(f"  {'OK' if hello else 'NG'}  PC → PSoC 6: 頼んだ hello に返事した（LED の点滅も目で確かめる）")
    print(f"  {'OK' if button else '--'}  SW2 の押下が届いた" + ("" if button else "（押していなければ、もう一度）"))
    print(f"  {shape}  データの形が StickS3 と同じ（type・dev・seq・t_ms）")
    if not hello:
        if micropython_only:
            print("\nMicroPython は動いているが、main.py が動いていない。README の手順 2 で main.py を置いて、RESET を押す")
        else:
            print("\n何も届かなかった。MicroPython と main.py が入っているか（README の手順 1・2）、RESET を押してから試す")
    return 0 if hello else 1


if __name__ == "__main__":
    sys.exit(main())
