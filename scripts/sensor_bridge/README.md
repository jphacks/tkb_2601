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
| 300 mg 以上が 6 回続く | 能力は制限、送る最終状態は Hold（停止して人を待つ） |
| データが 3 秒届かない（送信間隔 1 秒のとき） | 保持（停止） |
| KEY2 | 正常に戻し、保持を解く |

揺れが収まっても自動では戻らない。戻すのは KEY2（人）だけ。

注意ではスープを外して許された仕事を続ける。制限では能力区分を Limited としたまま Hold を送り、KEY2 で人が戻すまで停止する。しきい値未満の測定が2回続くと連続回数を数え直すが、段階そのものは自動では下がらない。

### 主なオプション

| オプション | 既定 | 内容 |
|---|---|---|
| `--robot` | `B` | 安全レベルを変えるロボット |
| `--serial` | 自動 | StickS3 のポート（例: `/dev/cu.usbmodem101`、`COM5`）。省略すると Espressif の USB 機器を探す |
| `--threshold-mg` | `300` | 異常とみなす振動（mg） |
| `--warning-count` / `--limited-count` | `2` / `6` | 異常が何回続いたら注意 / 制限にするか |
| `--unity-port` | `47610` | Unity が接続してくるポート |
| `--verbose` | なし | 測定値を 1 件ずつ表示する |
| `--simulate` | なし | USB シリアルを開かず、段階確認用の測定値と KEY2 イベントを作る |
| `--loop` | なし | `--simulate` の段階確認を繰り返す |

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

## 段階の確かめ（StickS3 を揺らさずに 3 段階を確かめる）

StickS3 本体には何もしない。中継が StickS3 と同じ形の行を自分で作り、正常 → 注意 → 制限 → KEY2 で正常 を流す。

Unity 受信側 `GuardianNetworkBridge` はこの公開版に含まれる。以下の自動確認は、Unity と実機を使わず、中継が TCP で送る状態を確認する。模擬入力を起動して Unity で Play すると、受信した B の状態を確認できる。通信断の保持は、データが戻ったあと Unity 左上の `Resume (checked by a person)` で解除する。人が押した STOP は別に残る。

macOS / Linux:

```bash
python3 scripts/sensor_bridge/check_stages.py
python3 scripts/sensor_bridge/sensor_bridge.py --simulate --loop
```

Windows（PowerShell）:

```powershell
# Unity なしで自動で確かめる（約 10 秒。OK / NG を表示）
.venv\Scripts\python scripts\sensor_bridge\check_stages.py
# 模擬入力を繰り返す（Unity で Play して B を確認する。Ctrl+C で止める）
.venv\Scripts\python scripts\sensor_bridge\sensor_bridge.py --simulate --loop
# 一括起動の中で両方を行う
powershell -ExecutionPolicy Bypass -File core\host\demo.ps1 -StageTest
```

`check_stages.py` は空いているポートと一時ログを使い、約 10 秒で確認して中継を終了する。合格時は終了コード 0、段階の順序が違う・保持になる・接続できない場合は 1 を返す。確認用の KEY2 は模擬イベントであり、実機のボタンを操作しない。

`--loop` を付けない模擬入力は 1 周後も静かな値を送り続け、入力終了だけで保持にならないようにする。中継自体は Ctrl+C で終了する。

一括起動には Docker Desktop と `.venv` が必要。PSoC 6 も使わない場合は `-Keyboard -NoPsoc` を追加する。既存の中継が動いている場合、`-StageTest` は模擬入力へ切り替えずに終了する。

一括起動は毎回 core のイメージを作り直し、ビルド・データベース準備・宛先登録が失敗したら、エラーを表示して停止する。初回のビルドにはインターネット接続が必要。
