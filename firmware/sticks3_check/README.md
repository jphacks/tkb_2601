# M5StickS3 動作確認ファームウェア

IMU（BMI270）の振動・傾きと、ボタン操作を JSON Lines で USB シリアルへ出す。

## ボタン

| ボタン | 位置 | 動作 |
|---|---|---|
| KEY1 | 正面 | 押して 1 秒待ち、3 秒間測って振動と姿勢の基準値にする |
| KEY2 | 右側面 | 押下イベントを出す |
| 電源 | 左側面 | 1 回押す: 起動・リセット / 2 回押す: 電源オフ / 長押し: 書き込みモード（緑の LED が点滅）。このプログラムでは使わない |

## 出力

送信間隔（既定 1000 ms）ごとに `sample`、ボタンなどはすぐに `event` を 1 行ずつ送る。

```json
{"type":"sample","dev":"sticks3-01","seq":14,"t_ms":12128,"win_ms":1000,"n":176,"vib_rms_g":0.0021,"vib_peak_g":0.0048,"ratio":0.94,"tilt_deg":0.0,"gx":0.923,"gy":-0.015,"gz":0.299,"bat":100}
{"type":"event","dev":"sticks3-01","seq":7,"t_ms":8028,"event":"button","btn":"KEY1"}
```

送信間隔は `{"cmd":"interval","ms":500}` を 1 行送ると変わる（100〜60000 ms）。

Mac では `arduino-cli monitor` では出力が見えなかった。`cat` で読む:

```bash
stty -f /dev/cu.usbmodem101 115200 raw -echo && cat /dev/cu.usbmodem101
```

## 書き込み

esptool の自動リセットを使うと StickS3 が USB から消えるので、手動で書き込みモードに入れて `--before no-reset` で書き込む。

1. ビルド:
   ```bash
   arduino-cli compile -b m5stack:esp32:m5stack_sticks3 --output-dir /tmp/sticks3_build firmware/sticks3_check
   ```
2. 左側面の電源ボタンを長押しし、緑の LED が点滅したら離す
3. 書き込み（ポート名は `ls /dev | grep usbmodem` で確認）:
   ```bash
   ~/Library/Arduino15/packages/m5stack/tools/esptool_py/*/esptool --chip esp32s3 -p /dev/cu.usbmodem101 --before no-reset --after no-reset write-flash 0x10000 /tmp/sticks3_build/sticks3_check.ino.bin
   ```
4. 左側面の電源ボタンを 1 回押して起動する

初回はボードの出荷時ファームウェアごと書き換わるため、`write-flash 0 /tmp/sticks3_build/sticks3_check.ino.merged.bin` で全体を書く。

## Windows で使う場合（未検証）

上の手順は macOS で確認したもの。Windows では次の点が変わる。

- 環境: Arduino IDE 2 か arduino-cli に、ボードマネージャーの URL `https://static-cdn.m5stack.com/resource/arduino/package_m5stack_index.json` を追加し、`M5Stack`（m5stack:esp32）と `M5Unified` を入れる。ボードは `M5StickS3`
- ポート: デバイスマネージャーの「ポート (COM と LPT)」で COM 番号を確認する
- esptool: `%LOCALAPPDATA%\Arduino15\packages\m5stack\tools\esptool_py\<バージョン>\esptool.exe`。`--before no-reset` を付けるのは同じ
- ビルド先: `/tmp/sticks3_build` の代わりに `%TEMP%\sticks3_build` などリポジトリの外を使う
- シリアルの確認: Arduino IDE のシリアルモニター（115200 bps）、または PowerShell:
  ```powershell
  $p = New-Object System.IO.Ports.SerialPort COM5,115200; $p.DtrEnable = $true; $p.Open(); while ($true) { $p.ReadLine() }
  ```
