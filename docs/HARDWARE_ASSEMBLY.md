# 実機連携ガイド（M5StickS3）

ロボもりの実機連携で使用する端末は、M5Stack の M5StickS3 です。内蔵の ESP32-S3-PICO-1-N8R8 と BMI270 を使い、USB で PC へ測定値を送ります。

この文書は、センサー連携の構成と準備を説明します。ファームウェア・Python 中継・Unity の接続受け口を収録しています。実機なしの模擬入力と通信仕様は [接続手順](STICKS3_UNITY_LINK.md)、公開シミュレーター単体の起動は [セットアップ](SETUP.md) を参照してください。実機での確認結果は、模擬入力による検証と分けて記録します。

## 接続構成

```text
M5StickS3（BMI270）
  → USB シリアル / JSON Lines
  → PC の Python 中継
  → TCP 127.0.0.1:47610 / JSON Lines
  → Unity の GuardianNetworkBridge
```

ESP32-S3-PICO-1-N8R8 は M5StickS3 内蔵のマイコンです。別の開発ボードを追加する構成ではありません。センサーから PC へは USB 有線、PC 内の中継から Unity へはループバック TCP を使います。

## 用意するもの

| 物 | 用途 |
|---|---|
| M5StickS3 1 台 | 内蔵 BMI270 の加速度計測と KEY1 / KEY2 操作 |
| データ通信対応の USB-C ケーブル | PC への接続と給電 |
| PC | Python 中継と Unity の実行 |
| 固定具 | 対象物へ取り付ける場合の位置・向きの保持 |

実装対象は M5StickS3 のみです。他機種への置き換えや、外付けセンサーの追加を前提にしません。

## 準備と取り付け

1. [M5StickS3 のファームウェア](../firmware/sticks3_check/README.md) を用意します。Arduino のボード指定は `m5stack:esp32:m5stack_sticks3`、使用ライブラリは M5Unified です。
2. データ通信対応の USB-C ケーブルで PC に接続し、シリアルポートを確認します。
3. 取り付けて計測する場合は、端末が滑ったり向きが変わったりしないよう固定します。
4. USB シリアルの JSON Lines が届くことを確認し、中継を起動してから Unity で Play します。

シリアル通信は 115200 bps、測定値の送信間隔は既定で 1 秒です。中継の起動方法は [センサー中継](../scripts/sensor_bridge/README.md)、共有用のボタンは [PSoC 6 パネル](../firmware/psoc6_share_panel/README.md) を参照してください。

## ボタンと状態の確認

| 操作・入力 | 実装の動き |
|---|---|
| KEY1 | 振動と姿勢の基準値を計測する。中継のレベル判定には使用しない |
| 振動が続く | 中継が振動 RMS を判定し、対象ロボットを Warning / Limited にする |
| センサーのデータが途切れる | 中継が対象ロボットを Hold にする |
| データが届いている状態で KEY2 | 中継が Normal を送り、中継由来の Hold を解除する |
| Unity で人が STOP を押す | センサーからの Normal では解除しない |

Warning はスープを外して他の仕事を続けます。センサーが Limited を判定した場合は Hold として停止し、KEY2 を待ちます。中継自体が止まった場合の停止と再開は、センサーのデータ途絶による Hold と区別します。

## 確認する項目

- USB シリアルから測定値が届くこと
- 振動が続いたときに Warning / Limited へ変わること
- データ途絶で Hold になり、復帰後も KEY2 を押すまで保持されること
- 人が Unity で押した STOP がセンサー入力で解除されないこと

測定値と判定への使い方は [使用センサーと測定上の注意](REAL_SENSOR_OPTIONS.md) にまとめています。

## 参照

- [M5Stack StickS3 公式仕様](https://docs.m5stack.com/en/core/StickS3)
- [M5Unified](https://github.com/m5stack/M5Unified)
