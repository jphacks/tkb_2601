# PSoC 6 パネル（CY8CPROTO-062-4343W / MicroPython）

人が SW2 で承認するためのボタンと、PC が点け消しするランプ。USB（KitProg3 の USB シリアル）で PC の [共有中継](../../scripts/guardian_share/README.md) とつなぐ。PSoC 6 自身はインターネットにつながない。

| 部品 | ピン | 使い方 |
|---|---|---|
| SW2（USER ボタン） | P0_4 | 押すと承認（共有待ちがなければ、届いた返信を確認済みにする） |
| ユーザー LED | P13_7 | 共有中継が決めた点灯・消灯をそのまま出す。`LED_ON = 0`（0 で点灯）。点き方が逆なら `main.py` の `LED_ON` を 1 にする |

## 書き込み（初回だけ）

[Infineon の MicroPython](https://ifx-micropython.readthedocs.io/en/latest/psoc6/installation.html) を入れてから、`main.py` を置く。

**Windows で PC に Python を入れないとき**は、PowerShell だけで両方を行う（Infineon の `mpy-psoc6.py device-setup` と同じ手順。公式の openocd と MicroPython を取ってきて書き込み、COM ポート越しに `main.py` を置いて、返事を確かめる）。PSoC 6 の鍵（`core\start.ps1`）は先に Ctrl+C で止める。

```powershell
powershell -ExecutionPolicy Bypass -File core\host\psoc6_setup.ps1              # MicroPython を書き込み、main.py を置く
powershell -ExecutionPolicy Bypass -File core\host\psoc6_setup.ps1 -SkipFlash   # main.py だけ置き直す
powershell -ExecutionPolicy Bypass -File core\host\psoc6_setup.ps1 -UpdateKitProg   # 書き込みに失敗したとき（KitProg3 を新しくしてから）
```

Mac / Linux（または Python を使ってよい PC）は、次の手順。

1. MicroPython を書き込む（書き込み用の openocd と最新のファームウェアを自動で取ってくる）
   ```bash
   pip install requests mpremote
   curl -s -L https://raw.githubusercontent.com/infineon/micropython/ports-psoc6-main/tools/psoc6/mpy-psoc6.py > mpy-psoc6.py
   python mpy-psoc6.py device-setup -b CY8CPROTO-062-4343W
   ```
   失敗したら `--kitprog-fw-update` を付けて、KitProg を 3 に更新してから書き込む。
2. `main.py` を置いて、リセットする（ポート名は macOS なら `/dev/cu.usbmodem…`、Windows なら `COM…`）
   ```bash
   mpremote connect /dev/cu.usbmodemXXXX fs cp firmware/psoc6_share_panel/main.py :main.py
   mpremote connect /dev/cu.usbmodemXXXX reset
   ```
   Thonny のファイル画面から置いてもよい。

`main.py` を直したいのに起動して邪魔をするときは、USER ボタンを押したまま RESET を押す（LED が 2 回点滅したら離す）。`main.py` を実行しないセーフモードで起動する。

共有中継が動いているあいだは、ポートを取り合うので mpremote や Thonny ではつなげない。

## つないで確かめる（Windows / macOS）

PSoC 6 を USB に挿して、PC と行き来できるかを確かめる。M5StickS3 が挿さっていても開かない（触らない）ので、そのままでよい。

1. キットの **KitProg3 側の USB 端子**（書き込み器がある側）と PC を、データ通信できるケーブルでつなぐ。
   - Windows 10 / 11 は、追加のドライバーなしで COM ポートとして見えるはず。デバイスマネージャーの「ポート (COM と LPT)」に KitProg3 USB-UART が出る。
   - macOS は `/dev/cu.usbmodem…` として見える。
2. 初めてなら、上の「書き込み」で MicroPython と `main.py` を入れる。
3. 確かめるプログラムを動かす（pyserial が必要）。

   macOS:
   ```bash
   python3 -m venv .venv
   .venv/bin/pip install -r scripts/guardian_share/requirements.txt
   .venv/bin/python scripts/guardian_share/psoc6_check.py
   ```
   Windows（PowerShell）:
   ```powershell
   py -m venv .venv
   .venv\Scripts\pip install -r scripts\guardian_share\requirements.txt
   .venv\Scripts\python scripts\guardian_share\psoc6_check.py
   ```
4. LED が 0.5 秒ごとに点滅するのを見ながら、SW2 を 1 回押す。最後に次のような結果が出れば、行き来できている。
   ```
     OK  USB で見えた（COM5）
     OK  PSoC 6 → PC: hello が届いた
     OK  PC → PSoC 6: 頼んだ hello に返事した（LED の点滅も目で確かめる）
     OK  SW2 の押下が届いた
     OK  データの形が StickS3 と同じ（type・dev・seq・t_ms）
   ```

| うまくいかないとき | 見ること |
|---|---|
| `PSoC 6 が見つからない` | 挿した端子が KitProg3 側か、ケーブルがデータ通信に対応しているか。`--list` でポートを並べられる |
| `開けなかった` | 共有中継・監視ハブ・Thonny・mpremote・ModusToolbox がポートを使っていないか |
| `MicroPython は動いているが、main.py が動いていない` | 手順 2 で `main.py` を置いて、RESET を押す |
| 何も届かない | MicroPython が入っているか（手順 1）。RESET を押してからもう一度 |
| LED が逆に光る（点灯と消灯が反対） | `main.py` の `LED_ON` を 1 にする |

## やり取り（JSON Lines、115200 bps）

M5StickS3（[firmware/sticks3_check](../sticks3_check/sticks3_check.ino)）と同じ形にしてある。機器から PC へは `type`・`dev`・`seq`・`t_ms` を必ず付け、PC から機器へは `cmd` で指示する。

PSoC 6 → PC:
```json
{"type": "event", "dev": "psoc6-01", "seq": 1, "t_ms": 812, "event": "hello", "fw": "psoc6-panel-2"}
{"type": "event", "dev": "psoc6-01", "seq": 2, "t_ms": 15230, "event": "button", "btn": "SW2"}
```

PC → PSoC 6:
```json
{"cmd": "lamp", "on": true}
{"cmd": "hello"}
```

| 項目 | 意味 |
|---|---|
| `type` | データの種類。PSoC 6 は測定値を送らないので、いつも `event` |
| `dev` | どの機器か（`psoc6-01`） |
| `seq` | 通し番号。抜けがあれば、途中の行が届かなかったか、PSoC 6 が再起動したとわかる |
| `t_ms` | PSoC 6 が起動してからの時間（ミリ秒） |
| `event` | `hello`（起動したとき、`{"cmd":"hello"}` が届いたとき、ランプの指示 10 回ごと）か `button` |
| `btn` | 押されたボタン（`SW2`） |

ランプの指示は PC から約 0.2 秒ごとに届き、点滅のリズムは PC が決める。前の形の `{"type": "lamp", "on": true}` も受け付ける。

この MicroPython は標準入力のポーリングに対応していないため、PC からの行を `readline()` で待って回す。ボタンは割り込みで数えておき（待っている間も割り込みは処理される）、次の行が届いたときに送る。0.4 秒より短い間隔の押下はチャタリングとして 1 回に数える。

**まだ確かめていないこと**: 実機での動作（ピン名、LED の向き、割り込みの動き）。CPython 上で `machine` をまねた部品と、仮想のシリアルポート越しに `psoc6_check.py` で確かめている。
