# 実機センサー候補（M5Stack / Infineon）

JPHACKS 2026 の提供機材から、Guardian の「機体の状態 → 仕事の可否判定 → 再割り当て」を実測値で動かすための候補を整理する。審査・賞は [JPHACKS_JUDGING_AWARDS.md](JPHACKS_JUDGING_AWARDS.md) を参照。

数値はメーカー公表のデータシート値。実際の精度は取付け方法、ロボットの走行状態、会場の騒音で決まるため、当日のキャリブレーションが前提。【未確認】は一次情報で確かめられていない項目。

## 結論

- **振動（主信号）は Bosch BMI270 で測る。** E84 AI キット、PSoC 6 AI キット、M5Stack CoreS3 / StickS3 / ATOMS3R-CAM のどれにも同じチップが載っている。ノイズは約 1 mg rms で、ロボットの振動（数十〜数百 mg）より十分小さい。精度を決めるのはセンサー性能ではなく、取付けの剛性とベースラインの取り方。
- **Infineon の技術として前面に出せるのは、マイク（XENSIV MEMS）、60 GHz レーダー、PSOC Edge と DEEPCRAFT のエッジ AI。** Infineon は IMU を作っていないため、振動だけでは Infineon 賞の「Sense」を言い切れない。
- **本命は KIT_PSE84_AI（PSOC Edge E84 AI Kit）。** IMU の 100 Hz データと、既製モデル（Factory Alarm、Siren、Fall など）の推論結果を、公式コード例が USB の仮想 COM ポートへ出す。難易度は★★★で、ModusToolbox の導入と KitProg3 の更新を Hack Day 前に済ませることが前提。
- **E84 が届かない場合に備えて、M5StickS3（BMI270 搭載、¥4,290）を確保しておく。** Unity 側の受け口は共通にして、センサーの出所だけを差し替えられるようにする。

## 提供機材と Guardian の信号

### Infineon（申請：第1 E84 AI Kit、第2 PSOC6 EVK、第3 MEMS Mic DIGITAL）

| ボード | 主なセンサー | Guardian での使い道 | 2日での難易度 |
|---|---|---|---|
| **KIT_PSE84_AI**（E84 AI Kit） | BMI270（IMU）、BGT60TR13C（60 GHz レーダー）、DPS368（気圧）、IM73D122 ×2（デジタルマイク）、IM73A135 ×2（アナログマイク）、BMM350（磁気）、SHT40（温湿度）、Ethos-U55 NPU、Wi-Fi 6 / BT 5.4 | 振動、異常音・警報音のエッジ AI 判定、作業エリアの人検知 | ★★★ |
| PSOC6 EVK for Sensor boards | **表記があいまい**。CY8CKIT-062S2-AI（BGT60TR13C、BMI270、BMM350、DPS368、IM72D128 ×2）なら有力。CY8CPROTO-062-4343W ならセンサーは実質マイクのみ | AI キットなら、レーダー存在検知の公式コード例がある | ★★ |
| EVAL-AHNB-DIGITALV01 | IM72D128（PDM マイク） | **PC から USB マイク（48 kHz / 24 bit）として見え、ファームウェア不要。** 異常音を PC 側で判定 | ★ |
| KIT-DPS368-2GO | DPS368 気圧 | 落下・持ち上げの補助程度 | ★★ |
| TLE493D-P3XX-MS2GO | 3D ホールセンサー | 磁石で車輪回転を測り、指令速度との差からスリップや停止を検知 | ★ |
| KIT_PSC3M5_MC1 ほか | モーター制御、タッチ | 今回の範囲外 | ★★★ |

### M5Stack（提供は1種類につき全体で約3個）

| 製品 | 主なセンサー | Guardian での使い道 |
|---|---|---|
| **CoreS3** | BMI270 + BMM150、近接センサー、カメラ、マイク、タッチ画面 | 振動・傾き・衝撃。本体画面に状態と「再開」ボタン |
| **M5StickS3** | BMI270、マイク、約 20 g | 機体に貼れる小型の振動センサー。Grove 給電には `M5.Power.setExtOutput(true)` が必要 |
| ATOMS3R-CAM | BMI270 + BMM150、カメラ、バッテリーなし | 振動とカメラ |
| M5GO V2.7 キット | MPU6886（IMU）、PIR、ENV III、Angle | 固定設置の「ステーション」。走行中の機体に PIR を載せると、自分の動きで誤検知する |
| ToF [U010] | VL53L0X | 前方の障害物距離。実用は約 1.2 m まで |
| 非接触温度 [U028] | MLX90614 | モーター表面温度。黒テープを貼り、1〜2 cm の距離で測る |
| ENV Pro / CO2 / GPS / 光 | — | Guardian との関連は低い |

提供リストにない追加候補：INA226 電流電圧ユニット ±10A（モーター電流）、計量ユニット 5kg（積載重量）、Hall ユニット（車輪回転）。在庫と価格は利用時に確認する。

## 精度とその意味

| センサー | 公表値 | 実際に効く要因 | 判定での使い方 |
|---|---|---|---|
| BMI270（IMU） | 加速度ノイズ 0.16 mg/√Hz（100 Hz で約 1 mg rms）、ゼロ g 誤差 ±20 mg、ODR 最大 1.6 kHz | 取付け剛性（両面スポンジは高い周波数を減衰させる）、速度や床面による変化。100 Hz 取得では 50 Hz までしか見えない | 1 秒窓の RMS をベースライン比で判定。重力成分はハイパスで除く |
| IM72D128 / IM73D122（マイク） | SNR 72〜73 dB(A)、AOP 128〜122 dBSPL | モーター音と会場の騒音が支配的。スピーカー再生の警報音は、実際の音より検出されにくい | 周波数帯エネルギーのベースライン比と、既製モデルの判定 |
| BGT60TR13C（レーダー） | 距離分解能 約 3 cm（理論値）、検出 0.2〜15 m | 存在検知は**固定設置が前提**。機体に載せると自己運動で誤検知する | 作業エリアに固定し、人の接近で減速または Hold |
| VL53L0X（ToF） | ±3〜7%（距離と反射率による） | 黒い面、ガラス、床の反射 | 30 cm 未満で減速など |
| MLX90614（非接触温度） | 室温付近 ±0.5 °C、ノイズ 0.05 °C rms | 視野 90° と広い。金属光沢面は低く読まれる | 50 °C で Warning、65 °C で長距離の仕事を Limited（例） |
| DPS368（気圧） | ノイズ 0.2 Pa rms（約 2 cm）、相対精度 ±6 Pa | 空調とドアの開閉、温度係数 0.5 Pa/K | cm 単位の高さ判定は無理。フロア変化や落下の補助 |

## 接続の構成

```
センサー基板 ──USB 仮想COM / USB オーディオ──▶ PC の Python ブリッジ ──UDP(JSON)──▶ Unity
  E84: sensor-hub-imu のコード例（IMU 100 Hz）       受信時刻（単調時計）を付ける           受信部 → SubmitVibration
       ready-model のコード例（推論結果）           1 秒窓の RMS と比率を計算              鮮度切れ → SetEmergencyStop
  AHNB: USB マイク                                  異常音のスコアを計算
  M5StickS3: JSON Lines を USB / Wi-Fi で送信
```

- JSON の例：`{"dev":"pse84-01","seq":812,"t_ms":456789,"metric":"vib_rms","unit":"g","v":0.031,"ok":1}`
- 現行の Unity では、実測値は `GuardianSimulator.SubmitVibration(robotId, 0〜1)`（`Assets/Scripts/GuardianSimulator.cs`）に入る。
- 実機 1 台を、シミュレーター内のロボット 1 台に対応させる。残りはシミュレーションのまま動かし、画面上で「実測」と「模擬」を区別して表示する。

## Hack Day 前と当日の分担

ガイドラインでは技術の準備は認められている一方、完成度（審査項目 3）は Hack Day 中の実装だけが評価対象。

| 時期 | 内容 |
|---|---|
| Hack Day 前（準備） | ModusToolbox 3.7 以上（Windows、DEEPCRAFT Studio も Windows 専用）を導入し、E84 のコード例をビルドまで確認する。KitProg3 の更新手順を確認する。M5StickS3 を確保する。機材の受け取り方法を Slack で確認する |
| Hack Day（成果） | Python ブリッジと Unity 受信部、ベースラインの取得、状態判定への受け渡し、実測と模擬の表示、実機を使ったデモ |

## 出典

- KIT_PSE84_AI 製品ページ：https://www.infineon.com/evaluation-board/KIT-PSE84-AI
- KIT_PSE84_AI クイックスタートガイド：https://www.infineon.com/assets/row/public/documents/30/44/infineon-kit-pse84-ai-qsg-usermanual-en.pdf
- IMU のコード例：https://github.com/Infineon/mtb-example-psoc-edge-sensor-hub-imu
- 既製モデルのコード例：https://github.com/Infineon/mtb-example-psoc-edge-ml-deepcraft-deploy-ready-model
- CY8CKIT-062S2-AI：https://www.infineon.com/evaluation-board/CY8CKIT-062S2-AI
- PSoC 6 レーダー存在検知のコード例：https://github.com/Infineon/mtb-example-psoc6-radar-presence
- EVAL-AHNB-DIGITALV01：https://www.infineon.com/evaluation-board/EVAL-AHNB-DIGITALV01
- BGT60TR13C データシート：https://www.infineon.com/assets/row/public/documents/30/49/infineon-bgt60tr13c-datasheet-en.pdf
- DPS368 データシート：https://www.infineon.com/assets/row/public/documents/30/49/infineon-dps368-datasheet-en.pdf
- BMI270 データシート：https://www.bosch-sensortec.com/media/boschsensortec/downloads/datasheets/bst-bmi270-ds000.pdf
- M5Stack 提供製品リスト：https://share.kwork.cloud/share/_5xPI5AXjXFB-I3VhGPWTDSUFjHe6Hs7riy6tAdK7fuD
- M5StickS3：https://docs.m5stack.com/en/core/StickS3 / https://www.switch-science.com/products/10921
- M5Stack CoreS3：https://docs.m5stack.com/en/core/CoreS3
- ToF / 非接触温度：https://docs.m5stack.com/en/unit/tof / https://docs.m5stack.com/en/unit/ncir
- VL53L0X データシート：https://www.st.com/resource/en/datasheet/vl53l0x.pdf
- MLX90614 データシート：https://www.melexis.com/-/media/files/documents/datasheets/mlx90614-datasheet-melexis.pdf
- ModusToolbox インストール：https://documentation.infineon.com/modustoolbox/docs/uqy1738949056062
