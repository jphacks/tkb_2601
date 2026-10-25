# センサー中継（M5StickS3 → Unity）

M5StickS3 の振動から、Unity シミュレーターのロボット 1 台の安全レベル（正常 / 注意 / 制限 / 保持）を決めて送る。既存の受け口 `GuardianNetworkBridge` のTCPポートとJSON Lines形式は変えずに使用する。要件は [docs/STICKS3_UNITY_LINK.md](../../docs/STICKS3_UNITY_LINK.md)。

```
StickS3 ──USB──▶ sensor_bridge.py ──TCP 127.0.0.1:47610──▶ Unity（GuardianNetworkBridge）
```

## 準備（初回だけ）

StickS3 に [firmware/sticks3_check](../../firmware/sticks3_check/README.md) を書き込んでおく。Python 3.10 以上を使う。

macOS:

```bash
python3.12 -m venv .venv
```

```bash
.venv/bin/pip install -r scripts/sensor_bridge/requirements.txt
```

Windows（PowerShell）:

```powershell
py -3 -m venv .venv
```

```powershell
.venv\Scripts\pip install -r scripts\sensor_bridge\requirements.txt
```

`.venv/` は Git の管理対象外。

## 使い方

1. StickS3 を USB でつなぐ
2. リポジトリのルートで中継を起動する

   macOS:
   ```bash
   .venv/bin/python scripts/sensor_bridge/sensor_bridge.py
   ```
   Windows:
   ```powershell
   .venv\Scripts\python scripts\sensor_bridge\sensor_bridge.py
   ```
3. Unity でシーンを開いて Play を押す。Unity が中継に接続し、最初の有効なデータから表示板「任せている仕事」のロボット B が正常で動き始める
4. 終わるときは中継のウィンドウで Ctrl+C

中継は、StickS3 のデータが届くまで Unity に何も送らない（Unity は中継なしの通常の動作のまま）。最初の有効なデータが届いたら正常で送り始める。データが途切れて保持になった場合は、データ復帰後に KEY2 を押すと正常を送り、中継由来の保持を解除する。KEY2 はデータが届いているときだけ受け付ける。

### 判定

| StickS3 の振動（1 秒ごとの RMS） | ロボット |
|---|---|
| 300 mg 以上が 2 回続く | 注意（スープを運ばない） |
| 300 mg 以上が 6 回続く | 制限（空いたお皿の回収だけ） |
| データが 3 秒届かない（送信間隔 1 秒のとき） | 保持（停止） |
| KEY2 | 正常に戻し、保持を解く |

揺れが収まっても自動では戻らない。戻すのは KEY2（人）だけ。

注意・制限は能力の制限であり、Unityの判断画面による停止は行わない。停止するのはHold、手動のSTOP、または中継全体の無通信だけ。

### 主なオプション

| オプション | 既定 | 内容 |
|---|---|---|
| `--robot` | `B` | 安全レベルを変えるロボット |
| `--serial` | 自動 | StickS3 のポート（例: `/dev/cu.usbmodem101`、`COM5`）。省略すると Espressif の USB 機器を探す |
| `--threshold-mg` | `300` | 異常とみなす振動（mg） |
| `--warning-count` / `--limited-count` | `2` / `6` | 異常が何回続いたら注意 / 制限にするか |
| `--unity-port` | `47610` | Unity が接続してくるポート |
| `--verbose` | なし | 測定値を 1 件ずつ表示する |

例: ロボット C を対象に、500 mg 以上が 3 回で注意、8 回で制限にする

```bash
.venv/bin/python scripts/sensor_bridge/sensor_bridge.py --robot C --threshold-mg 500 --warning-count 3 --limited-count 8
```

### 注意

- 中核システム（`guardian_net`）と同じポートを使うため、同時には動かさない
- 中継を止めると、Unity は 1.5 秒後に全ロボットを止める。中継を起動し直してから Unity の画面の「Resume (checked by a person)」を押すと戻る
- 対象ロボットのレベルを Unity のパネルで手動で変えても、0.5 秒後に中継の判定で上書きされる（人が押した STOP は除く）

## 記録

`Logs/sensor_bridge/` に起動ごとに 1 ファイル（JSON Lines）。測定値、レベルの変化と理由、KEY2、データの途切れ、Unity と StickS3 の接続を時刻付きで残す。`Logs/` は Git の管理対象外。

## テスト

判定の単体テスト:

```bash
.venv/bin/python -m unittest discover -s scripts/sensor_bridge
```

Unity を起動せずに通信を確かめるときは、中継を起動したまま別のウィンドウで `fake_unity.py` を動かす。Unity と同じように中継に接続し、届いた状態を表示する。

```bash
.venv/bin/python scripts/sensor_bridge/fake_unity.py 30
```
