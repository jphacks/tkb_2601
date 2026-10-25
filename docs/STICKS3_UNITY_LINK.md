# センサー中継と Unity の接続

M5StickS3 の振動を Python 中継で Normal / Warning / Limited / Hold に変換し、Unity の `GuardianNetworkBridge` に送る。ロボットは Unity の中にあり、この接続で実ロボットを制御するものではない。

## 実機なしで試す

1. [セットアップ](SETUP.md) に従って Unity 6000.3.12f1 で公開シーンを開く。
2. リポジトリのルートで `python3 scripts/sensor_bridge/sensor_bridge.py --simulate --loop` を起動する。Windows では `py -3` または `.venv\Scripts\python` を使う。
3. Unity で Play する。左上に `SENSOR LINK` が表示され、B が Normal → Warning → Limited → Normal と変わる。Warning はスープ、Limited は料理も外し、皿の回収は許す。
4. B に STOP をかけ、次の模擬 KEY2 で状態が Normal に戻っても、STOP が残ることを確認する。
5. 中継を止めると、最後の有効なパケットから既定 1.5 秒で全機を保持する。中継を起動し直し、データが届いたことを確認して、左上の `Resume (checked by a person)` を押す。別にかけた STOP は右側パネルで人が解除する。

模擬 KEY2 は試験用の入力で、実機のボタンを操作しない。実機なしの自動確認は `python3 scripts/sensor_bridge/check_stages.py`、中継の準備と実機接続は [センサー中継の説明](../scripts/sensor_bridge/README.md) を参照する。

## 実機をつなぐ

- M5StickS3: [ファームウェアと書き込み手順](../firmware/sticks3_check/README.md)。USB シリアルで測定値と KEY2 の押下を送り、中継が状態を判定する。
- PSoC 6: [パネルのファームウェアと準備](../firmware/psoc6_share_panel/README.md)。SW2 による共有の承認と LED の表示を扱う。Unity の能力状態は変更しない。

振動の判定条件は、既存の 300 mg・2回で Warning・6回で Limited のまま。しきい値を下回る測定が1回あれば連続回数を数え直す。静かになっただけでは段階を下げず、実機で Normal に戻すのはデータが届いている間の KEY2 とする。実際の安全限界や校正済みの値ではない。

## 通信仕様

Unity が TCP クライアント、中継がサーバーで、宛先は `127.0.0.1` に限定する。まず 47610 へ接続し、接続できない、または接続先から有効な状態が届かず切断した場合は 18610 を試す。ポートを変える場合は中継の `--unity-port` と Unity の `-robomoriPort=<番号>` を揃える。

UTF-8 の JSON を1行ごとに送り、LF または CRLF で区切る。

```json
{"v":1,"boot":"relay-run-id","seq":1,"robots":[{"id":"B","final":"Warning","capability":"Warning","trust":"Trusted","measured":true,"reasons":["振動が続いたため注意"]}]}
```

- `v` は 1。`boot` は中継を起動するたびに変える識別子で、`seq` は1から増やす。
- `robots` は空にせず、シーンにある ID を重複なく指定する。同じ `boot` の間は対象の ID 集合を変えない。
- `capability` は Normal / Warning / Limited。`final` は同じ状態または Hold。`trust` が Untrusted なら Hold が必要。
- 行は8192文字まで、理由は8件まで、各理由は512文字まで。IO の待ち行列は16件に抑え、過大な行は読み捨てて次の行から再開する。
- 不正な内容、同じ・古い `seq`、現在の中継が送信中なのに割り込む別の `boot` は反映しない。拒否した入力では通信監視の時刻を更新しない。

`boot` と `seq` は再送や混線を防ぐための確認で、暗号による相手認証ではない。同じ PC 上の悪意あるプロセスを、この接続だけで防げるとはしない。

## 停止と復帰

センサーの Hold と人の STOP は別に保存し、どちらかが残っていれば停止する。中継から Normal が来ても、解除できるのはセンサー由来の Hold だけ。人がセンサーの Hold 中に STOP を押した場合も、その STOP を残す。

中継自体が途切れた場合は全機を保持する。データの再開や中継の再起動だけでは解除せず、新しい有効な状態が届いたあと、人が Unity で復旧を確認する。シナリオのリセットでも、この保持を飛び越えない。接続中の対象機では手動の模擬状態入力を無視し、固定シードの故障イベントも停止する。歩行者安全停止は従来どおり別に判定する。

## 検証

ライセンス済み Unity 6000.3.12f1 で、次のメソッドをバッチ実行する。

`Guardian.UnitySim.Editor.GuardianNetworkBatch.Verify`

15ケースで、段階の反映、スープの再割り当て、停止の所有者の分離、通信断と人による復旧、再送・不正パケットの拒否、シナリオリセット、ループバック TCP の分割行・過大行・代替ポート・受信スレッドの終了を確認する。

既存の `GuardianIntegrationBatch.Verify` と [統合検証スクリプト](../scripts/Invoke-GuardianVerification.ps1) にも組み込んでいる。バッチモードでは実機用の中継へ自動接続せず、検証が用意したポートにだけ接続する。

GitHub Actions ではセンサー中継21件、共有と PSoC 6 の仮のファームウェア実行33件、Unity のメタデータと検証入口を確認する。Unity Editor、描画、実クリック、M5StickS3 のビルド・書き込み、実機の通信はクラウド CI の対象外。
