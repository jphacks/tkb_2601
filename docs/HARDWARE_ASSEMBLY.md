# 実機の組み立てガイド（Infineon / M5Stack）

申請したセンサーを、どんな形で、どう組み立てて PC の Guardian につなぐかの手順。センサーの選定理由と精度は [REAL_SENSOR_OPTIONS.md](REAL_SENSOR_OPTIONS.md) を参照。

- Infineon の申請：第1 PSOC Edge E84 AI Kit、第2 PSOC6 EVK for Sensor boards、第3 MEMS Mic（EVAL-AHNB-DIGITALV01）
- M5Stack の申請：**（申請した製品名をここに記入）**。この文書では M5StickS3、CoreS3、M5GO キット、ToF、非接触温度ユニットを想定する
- どれが届くかは未確定。届いた組み合わせ別の配置は「4. 届いた組み合わせ別の配置」を参照

情報源は各メーカーの公式マニュアル（末尾の出典）。【現物で確認】は文書に記載がなく、届いた実物で確かめる項目。

## 1. 全体の形

Guardian の「ロボット B の駆動部」を、机の上に置く試験台として作る。モーターにおもりを付けると異常振動と異常音が出る。センサーがそれを測り、Unity 内のロボット B の状態が変わって、仕事が別の機体に移る。

```
                 [PC] Unity の Guardian と Python ブリッジ
                   │USB-C(J1)        │Micro-USB         │USB-C / Wi-Fi
┌──────────────── 台板（合板や MDF、30×40 cm 程度）────────────────┐
│                                                                   │
│   [USB ファン or 小型 DC モーター] ← 羽根に付け外しできるおもり   │
│        ↑ 5〜10 cm の距離に AHNB のフレックスマイク（L）            │
│                                                                   │
│   [L 字金具に E84 を縦向きに固定]                                  │
│        表面（レーダー）→ 人が近づく側に向ける                      │
│        裏面（マイク）  → モーター側。スペーサーで浮かせてふさがない│
│                                                                   │
│   [M5StickS3] 背面の磁石で、台板に貼った鉄板に固定                 │
└───────────────────────────────────────────────────────────────────┘
      ↑ 床にテープで「作業エリア」の線（台から 1 m など）
```

- **モーターのアンバランスは、回転機械で実際に起きる故障のひとつ**で、振動監視の代表的な対象。ただしデモで作るのは異常の模擬なので、発表では「摩耗そのものを測った」とは言わない。
- 走るロボットを用意できる場合は、M5StickS3 を車体に載せて Wi-Fi で送る（E84 はバッテリー端子が未実装のため、USB でつないだまま使う）。

## 2. 自分で用意するもの

| 物 | 数 | 用途・注意 |
|---|---|---|
| USB-C ケーブル（データ通信対応） | 2〜3 本 | E84 の J1、PSoC 6、M5Stack 用。**充電専用ケーブルは不可**。E84 キットに同梱の USB-C ケーブルはカメラ用で、PC 接続に流用できるかは両端の形状次第【現物で確認】 |
| **Micro-USB ケーブル（データ通信対応）** | 1 本 | AHNB 用。**USB-C ではない** |
| セルフパワーの USB ハブ | 1 | PC のポート不足と電力不足の対策 |
| 長めの USB ケーブル（2 m） | 1 | 台と PC を離して置くため |
| USB ファン、または小型 DC モーター（例：FA-130）と電池ボックス | 1 | 振動と音の発生源。ファンが安全で扱いやすい |
| おもり（クリップ、硬貨、粘土）、マスキングテープ | 少々 | 羽根の1枚に付けてアンバランスを作る |
| 台板（合板や MDF、30×40 cm 程度） | 1 | 全部をここに固定する |
| L 字金具、M3 のネジ・ナット・スペーサー | 数組 | E84 と PSoC 6 を縦に固定する（取付穴の有無は【現物で確認】） |
| 結束バンド、ホットボンド | 少々 | 取付穴がない場合の固定。**部品の上には付けない** |
| 鉄板、または大きめのワッシャー | 1 | M5StickS3 の磁石を付ける場所 |
| 薄い硬めの両面テープ | 少々 | **スポンジ両面テープは振動の高い周波数を吸収するので、IMU には使わない** |
| 黒いビニールテープ | 少々 | 非接触温度センサーで金属面を測るとき |
| スマホのテザリング | — | 会場 Wi-Fi で ESP32（2.4 GHz）がつながらない場合の予備 |

## 3. 機材ごとの組み立て

### 3-1. PSOC Edge E84 AI Kit（KIT_PSE84_AI）

**箱の中身**：E84 AI ボードと、0.3 MP の USB カメラ（USB-C ケーブル付き）。Rev *A より前の版では、カメラが OV7675（J14 に挿すタイプ）になっている。届いたら版を確認する。

**センサーの位置**（クイックスタートガイドの基板図より）

| 面 | 部品 |
|---|---|
| 表 | 60 GHz レーダー BGT60TR13C（U5）、IMU BMI270（U18）、気圧 DPS368（U6）、温湿度 SHT40（U22）、磁気 BMM350（U20）、Wi-Fi / BT アンテナ |
| **裏** | デジタルマイク IM73D122 ×2（U7, U8、間隔 約 27 mm）、アナログマイク IM73A135 ×2（U9, U10、間隔 約 32 mm） |

**端子**

| 端子 | 用途 |
|---|---|
| **J1（USB-C）** | **KitProg3。電源、書き込み、UART（115200 bps）をこの1本でまかなう。PC にはここを挿す** |
| J2（USB-C） | MCU 側の USB。カメラや USB デバイス用。今回は使わない |
| J3 | バッテリー端子。**部品が実装されていない**（はんだ付けが必要）ので、USB 給電で使う |
| J5 / J7 / J14 / J16 / J8 | 拡張ヘッダー、I2C、スピーカーなど。**どれも部品が未実装**で、外部センサーの追加にははんだ付けが必要 |
| SW1 / SW2 / SW3 | ユーザーボタン / リセット / KitProg3 のモード切替 |

**取り付け**
1. マイクは裏面にあるので、**スペーサーで基板を浮かせて裏をふさがない**。
2. レーダーを使う場合は、L 字金具で**基板を縦に立て**、表面を人が近づく側へ向ける。裏面のマイクはモーター側を向く。
3. IMU で振動を拾うため、**台板に硬く固定する**。取付穴があればネジ、なければ結束バンドかホットボンドで、基板の縁だけを留める。
4. 台が揺れるとレーダーが誤検知することがある。当日、おもりを付けて回した状態で人がいないときに誤検知しないか確かめ、駄目ならレーダーは別の台（PSoC 6 か箱の上）に移す。

**最初の動作確認（ソフトの作成なしでできる）**
1. J1 を PC につなぐ。オレンジの LED5 が点き、緑の LED1 が点滅すれば、出荷時のデモが動いている。
2. Tera Term などで KitProg3 の COM ポートを開く（115200、8 bit、パリティなし、ストップ 1、フロー制御なし）。
3. SW2（黒いリセットボタン）を押すとメニューが出る。番号でデモを選び、`B` でメニューに戻る。

**開発環境**
- ModusToolbox 3.5 以上。IMU のコード例 `mtb-example-psoc-edge-sensor-hub-imu` は 3.7 以上が必要。
- KitProg3 のファームウェアは v2.80 以上が必要。
- DEEPCRAFT Studio 5.3 以上（Windows 専用）。

**使うコード例**

| コード例 | 出力 |
|---|---|
| `sensor-hub-imu` | IMU を 100 Hz で取得し、UART に出力 |
| `ml-deepcraft-deploy-ready-model` | 既製モデル（Factory Alarm、Siren、Fall など）の判定結果を UART に出力。推論回数に評価用の制限がある |

### 3-2. PSOC6 EVK for Sensor boards

**どのボードが届いたかを、まず型番で確認する。**

**A. CY8CKIT-062S2-AI（PSoC 6 AI Evaluation Kit）の場合**

- **箱の中身**：ボードと案内カード（QR コード）のみ。**USB-C ケーブルは自分で用意する**。
- **センサー**：レーダー BGT60TR13C（U6）、IMU BMI270（U18）、磁気 BMM350（U19）、気圧 DPS368（U3）、デジタルマイク IM72D128 ×2（U4, U5、間隔 約 27 mm）。

| 端子 | 用途 |
|---|---|
| J1（USB-C） | KitProg3。電源、書き込み、UART |
| J2（USB-C） | MCU 側の USB。出荷時のデータ収集ファームウェアが使う |
| J3 | **Li-ion バッテリー端子（実装済み）**。3.6〜4.2 V、充電電流は最大 190 mA。コネクタの形状は【現物で確認】 |
| J16 | Qwiic（I2C、3.3 V）。M5Stack の Grove とは形が違い、変換ケーブルが必要。Grove ユニットは 5 V 給電のものが多いので注意 |

- **役割**：**作業エリアの人検知センサーとして固定設置する**。公式のレーダー存在検知コード例 `mtb-example-psoc6-radar-presence` があり、UART に「存在 / 不在」と距離を出す。検知距離の上限は 0.66〜5.0 m で設定でき、既定は 2.0 m。
- **取り付け**：振動しない場所（台とは別の箱や三脚の上）に置き、作業エリアの方向へ向ける。ロボットや揺れる台に載せると、自分の動きで誤検知する。
- Arduino IDE 2 にも正式対応しているので、IMU だけなら ModusToolbox より早く動かせる。

**B. CY8CPROTO-062-4343W の場合**
- 載っているセンサーは実質 PDM マイク（とサーミスタ）だけ。外付けセンサーはジャンパー線での配線が必要で、2日間で使い込む価値は低い。Wi-Fi 付きのマイコンとして予備に回す。

### 3-3. MEMS Mic（EVAL-AHNB-DIGITALV01、Audiohub Nano Digital）

**形**：Audiohub Nano ボードと、IM72D128 を載せたフレックス（細長い基板）のマイク。同時に使えるのは左右2本。

| 端子・部品 | 用途 |
|---|---|
| **USB1（Micro-USB）** | 電源と音声の転送。**USB-C ではない** |
| X1 / X2 | 左 / 右チャネルのフレックスマイク用コネクタ |
| X3 | 外部 PDM マイク。**ここに挿すと X1 と X2 が無効になる** |
| S1 ボタン | ゲイン（0 / 12 / 18 / 24 dB）と電力モードの切替。**録音していないときだけ変えられる** |
| LED D1〜D12 | 音量メーター。緑 50〜80 dB SPL、黄 94、橙 110、赤 125 |

**組み立て**
1. フレックスマイクを X1（左）と X2（右）に差し込む。向きはマニュアルの図に合わせ、無理に曲げない。
2. 左のマイクはモーターから 5〜10 cm、右のマイクはモーターから離れた位置に置く。右を会場の騒音の基準にして、左との差でモーターの音を取り出せる（判定方法の案）。
3. ボードは台板にテープで留める。マイクの穴はふさがない。

**PC 側の設定**
1. Micro-USB で PC につなぐと、録音デバイス「AudioHubNano2D」として出てくる（48 kHz / 24 bit ステレオ）。
2. Windows では、マニュアルの推奨どおり「システムの詳細設定 → パフォーマンス → 詳細設定 → バックグラウンドサービス」を選ぶ。
3. まず Audacity で録音し、モーター音で LED が緑〜黄になるようにゲインを合わせる。赤は音が割れている。
4. その後、Python（`sounddevice` など）で取り込んで、異常音のスコアを計算する。基板上では AI は動かない。

### 3-4. M5StickS3

- **形**：48×24×15 mm、20 g。**背面に磁石**。250 mAh のバッテリー、BMI270（IMU）、マイク、1.14 インチ画面、KEY1 / KEY2。
- **取り付け**：台板に貼った鉄板に磁石で付ける。振動で滑らないよう、上から薄いテープで押さえる。ケースに軸の向きを書いておく。
- **Grove（Port A：G9 / G10、5 V）**：ToF などをつなぐ場合、M5Unified は初期状態で 5 V 出力を切るため、`M5.Power.setExtOutput(true);` が必要。
- **開発環境**：Arduino と M5Unified。PlatformIO なら board は `esp32-s3-devkitc-1`、`-DARDUINO_USB_CDC_ON_BOOT=1` を付ける（付けないと USB シリアルの出力が見えない）。書き込みモードはボタン長押しで入る。
- **送信**：USB シリアルで JSON を1行ずつ送るのが最短。ロボットに載せて動かすときは Wi-Fi UDP にする（2.4 GHz のみ）。

### 3-5. M5Stack CoreS3

- **形**：本体 54×54×15.5 mm、DIN ベース付きで 69×54×31.5 mm、72.7 g。500 mAh。
- **箱の中身**：本体、DIN ベース、2.5 mm 六角レンチ（M3 用）、ネジ留め金具 4 個、レール取付金具。
- **取り付け**：**DIN ベースを台板にネジ留めする。今回の機材の中で最も硬く固定でき、振動を正しく拾いやすい。** 画面には状態と「再開」ボタンを表示できる。
- **端子**：Port A（I2C、G2 / G1）、Port B（G9 / G8）、Port C（G17 / G18）。B と C の物理的な位置は【現物で確認】。
- **注意**：磁気センサー BMM150 は磁石の影響を受ける。モーターや StickS3 の磁石から離す（振動の判定には使わない）。
- **書き込みモード**：RESET を 3 秒長押しし、緑の LED が点いたら離す。

### 3-6. M5GO IoT スターターキット V2.7

- **中身**：本体（IMU MPU6886、500 mAh）と、ENV III、IR、RGB、PIR、Angle、Hub の各ユニット。
- **役割**：**固定設置の「ステーション」**として使う。
  - PIR（Port B）：作業エリアの入口に置き、人の出入りを見る。反応後 2 秒は出力が保持される。走るロボットに載せると自分の動きで誤検知する。
  - Angle（Port B）：オペレーターの操作ダイヤル。Guardian 側では `source=operator` として、センサーの値とは分けて扱う。
  - Hub（Port A）：I2C ユニットを複数つなぐための分岐。
- **注意**：Port A は本体の IMU（0x68）と同じ I2C バスにある。

### 3-7. ToF ユニット（U010）と非接触温度ユニット（U028）

| ユニット | 接続 | 置き方 |
|---|---|---|
| ToF（VL53L0X） | Port A、I2C 0x29 | 前方の障害物や人に向ける。実用は約 1.2 m まで。黒い面、ガラス、床の反射で値が乱れる |
| 非接触温度（MLX90614） | Port A、I2C 0x5A | モーターのケースから 1〜2 cm。視野が 90° と広いので近づける。光った金属面には黒テープを貼る |

2 つとも Port A なので、同時に使うには Hub（M5GO キットに同梱）で分岐させる。アドレスは衝突しない。

## 4. 届いた組み合わせ別の配置

| 届いたもの | 台の上（振動する側） | 台の外（固定） |
|---|---|---|
| E84 のみ | E84：IMU で振動、マイクで音、既製モデル | （レーダーは台が揺れても誤検知しなければ使う） |
| E84 と AHNB | E84：IMU で振動、既製モデル。AHNB：モーター音 | — |
| E84 と PSoC 6 AI | E84：IMU とマイク | PSoC 6 AI：レーダーで作業エリアの人検知 |
| E84 と M5StickS3 / CoreS3 | M5Stack：振動（最も簡単） | E84：レーダーとマイク、既製モデル |
| Infineon のボードが届かない | M5StickS3 / CoreS3：振動 | M5GO：PIR で人検知 |

## 5. PC まわり

- 使う USB ポート：E84 の J1（USB-C）、AHNB（Micro-USB）、M5Stack（USB-C）、PSoC 6（USB-C）。セルフパワーの USB ハブにまとめる。
- Windows で確認するもの：KitProg3 の COM ポート番号、録音デバイス「AudioHubNano2D」、M5Stack の COM ポート番号。
- データは Python ブリッジでまとめ、受信時刻を付けて UDP で Unity に送る（詳細は [REAL_SENSOR_OPTIONS.md](REAL_SENSOR_OPTIONS.md) の「接続の構成」）。

## 6. Hack Day 前のチェックリスト

ガイドラインでは技術の準備は認められている。完成度（審査項目3）は Hack Day 中の実装だけが評価対象なので、Guardian とつなぐ部分は当日作る。

- [ ] ModusToolbox 3.7 以上と DEEPCRAFT Studio を Windows PC に入れる（容量が大きい）
- [ ] データ通信できる USB-C と Micro-USB のケーブルを用意し、PC で認識するか確かめる
- [ ] 台板、ファンかモーター、おもり、L 字金具、ネジ、結束バンド、鉄板を買う
- [ ] 届いたら型番と版（E84 は Rev *A かどうか）、箱の中身を確認する
- [ ] E84：出荷時のデモをシリアル端末で動かす。KitProg3 を v2.80 以上にする
- [ ] AHNB：Audacity で録音し、ゲインを合わせる
- [ ] M5Stack：M5Unified のサンプルで IMU の値を画面に出す
- [ ] 会場で Wi-Fi が使えない場合に備え、USB 有線で全部動く構成にしておく

## 出典

- KIT_PSE84_AI クイックスタートガイド：https://www.infineon.com/assets/row/public/documents/30/44/infineon-kit-pse84-ai-qsg-usermanual-en.pdf
- KIT_PSE84_AI キットガイド：https://www.infineon.com/assets/row/public/documents/30/44/infineon-kit-pse84-ai-user-guide-usermanual-en.pdf
- KIT_PSE84_AI リリースノート：https://www.infineon.com/assets/row/public/documents/30/44/infineon-kit-pse84-ai-release-notes-usermanual-en.pdf
- CY8CKIT-062S2-AI キットガイド：https://www.infineon.com/assets/row/public/documents/30/44/infineon-cy8ckit-062s2-user-guide-usermanual-en.pdf
- PSoC 6 レーダー存在検知のコード例：https://github.com/Infineon/mtb-example-psoc6-radar-presence
- Audiohub Nano Digital ユーザーマニュアル：https://www.infineon.com/assets/row/public/documents/30/44/infineon-ug095149-audiohub-nano-digital-xensiv-mems-microphones-evaluation-board-usermanual-en.pdf
- IMU のコード例：https://github.com/Infineon/mtb-example-psoc-edge-sensor-hub-imu
- 既製モデルのコード例：https://github.com/Infineon/mtb-example-psoc-edge-ml-deepcraft-deploy-ready-model
- M5StickS3：https://docs.m5stack.com/en/core/StickS3
- M5Stack CoreS3：https://docs.m5stack.com/en/core/CoreS3
- M5GO V2.7：https://docs.m5stack.com/en/core/m5go_v2.7
- ToF / 非接触温度 / PIR：https://docs.m5stack.com/en/unit/tof / https://docs.m5stack.com/en/unit/ncir / https://docs.m5stack.com/en/unit/pir
