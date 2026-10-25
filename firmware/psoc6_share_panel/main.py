# ロボもり PSoC 6 パネル（CY8CPROTO-062-4343W / Infineon MicroPython）
#
# USB シリアル（KitProg3）で PC と JSON Lines をやり取りする。形は M5StickS3（firmware/sticks3_check）とそろえてある。
#   PSoC 6 → PC: {"type":"event","dev":"psoc6-01","seq":1,"t_ms":812,"event":"hello","fw":"psoc6-panel-2"}
#                    起動したとき、{"cmd":"hello"} が届いたとき、ランプの指示 10 回ごと（約 2 秒）
#                {"type":"event","dev":"psoc6-01","seq":2,"t_ms":15230,"event":"button","btn":"SW2"}
#                    SW2（USER ボタン）を押したとき
#   PC → PSoC 6: {"cmd":"lamp","on":true}   約 0.2 秒ごと。点滅のリズムは PC が決める
#                {"cmd":"hello"}            hello をすぐ返す（つながったかの確認用）
#   前の形の {"type":"lamp","on":true} も受け付ける。
#
# この移植版は標準入力のポーリング（select）に対応していない。そこで PC からの行を待つ readline() で回し、
# ボタンは割り込みで数えておく（readline() で待つ間も割り込みは処理される）。
# PC 側が動いていないあいだは readline() で待ち続け、押した回数は PC がつながってから 1 回にまとめて送る。
import json
import sys
import time

from machine import Pin

DEV = "psoc6-01"
FW = "psoc6-panel-2"
LED_ON = 0          # ユーザー LED（P13_7）は 0 で点灯する。点き方が逆なら 1 にする
DEBOUNCE_MS = 400   # これより短い間隔の押下は、チャタリングとして 1 回に数える

led = Pin("P13_7", mode=Pin.OUT, value=1 - LED_ON)
button = Pin("P0_4", mode=Pin.IN, pull=Pin.PULL_UP)  # SW2（USER ボタン）。押すと 0 になる

presses = 0


def on_press(pin):
    # 割り込みの中では数えるだけにする（文字列やリストを作らない）
    global presses
    presses += 1


button.irq(handler=on_press, trigger=Pin.IRQ_FALLING)

seq = 0


def send_event(event, **fields):
    global seq
    seq += 1
    record = {"type": "event", "dev": DEV, "seq": seq, "t_ms": time.ticks_ms(), "event": event}
    record.update(fields)
    print(json.dumps(record))


def set_lamp(on):
    led.value(LED_ON if on else 1 - LED_ON)


send_event("hello", fw=FW)
handled = 0
last_press = time.ticks_add(time.ticks_ms(), -DEBOUNCE_MS)
lamp_messages = 0

while True:
    line = sys.stdin.readline().strip()
    message = None
    if line.startswith("{"):
        try:
            message = json.loads(line)
        except ValueError:
            message = None
    if isinstance(message, dict):
        command = message.get("cmd") or ("lamp" if message.get("type") == "lamp" else None)
        if command == "lamp":
            set_lamp(message.get("on") is True)
            lamp_messages += 1
            if lamp_messages % 10 == 0:
                send_event("hello", fw=FW)
        elif command == "hello":
            send_event("hello", fw=FW)
    if presses != handled:
        handled = presses
        now = time.ticks_ms()
        if time.ticks_diff(now, last_press) >= DEBOUNCE_MS:
            last_press = now
            send_event("button", btn="SW2")
