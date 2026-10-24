# ロボもり — Unity配膳シミュレーター

## 製品概要

配膳ロボットの状態管理、仕事の割り当て・引き継ぎ、
店内の3Dシミュレーションを扱うUnityプロジェクトです。

## 解決する課題

飲食店で複数の配膳ロボットを運用すると、機体状態、積載物、通路の競合、
人の横断を同時に考慮しながら、止めるべき機体と継続できる仕事を判断する必要があります。
ロボもりは、状態に応じた能力制限と仕事の再割り当てを可視化し、
安全側に倒す判断をレストランの3Dデモで確認できるようにします。

## 特長

- ロボットの状態に応じた仕事の可否判定
- 配膳タスクの割り当てと再割り当て
- ロボットの移動・交通制御
- Unity Editorによるレストランシーン生成
- ローカル3Dモデルの任意インポート

## 公開デモで確認できること

| 操作・状況 | 確認できる動作 |
| --- | --- |
| Soup / Food / Returnタスクを追加 | 状態と能力マトリクスに従う割り当て |
| RobotをWarning / Limitedへ変更 | 不適格な仕事を避けた継続・再割り当て |
| 緊急停止と復帰 | 停止中の新規割り当て拒否と明示的な復帰 |
| `-guardianPedestrianDemo` | 横断予測による一時停止と通過後の再開 |
| `-guardianInteractionDemo` | 日本語の状態照会、停止・復帰の確認付き操作 |

審査での操作順、期待表示、説明上の注意は
[審査デモガイド](docs/JUDGING_GUIDE.md)にまとめています。

## 公開版について

このリポジトリには、公開対象として選んだ制御コード、
設定、Editorツール、レストラン素材を収録しています。

一部のモデル、アニメーション、音声、その他の素材は
公開版に含まれていません。

そのため、発表時の構成と公開版では外観や演出が
異なる場合があります。

公開版だけで発表時の全機能を完全再現できることは
保証していません。

詳しくは [公開範囲と検証](docs/PUBLIC_SCOPE.md) を参照してください。

提出前の確認状況は[提出チェックリスト](docs/SUBMISSION_CHECKLIST.md)、
段階的な開発差分は[開発履歴](docs/DEVELOPMENT_HISTORY.md)を参照してください。

## 動作環境

- Unity 6000.3.12f1
- Windows / macOS / Linux（環境ごとの動作は未検証）
- Unity Hub
- Git

## 起動方法

1. このリポジトリをcloneします。
2. Unity HubからUnity 6000.3.12f1で開きます。
3. Unityのインポートとコンパイル完了を待ちます。
4. Consoleにエラーがないことを確認します。
5. `Guardian > Scene Tools...` を開き、シーンがない場合は `Generate Scene` を実行します。
6. 同じ画面の `Validate Saved Scene` で公開素材・配置の検証を実行します。
7. `Assets/Scenes/GuardianRestaurant.unity` を開きます。
8. Playボタンでシミュレーションを開始します。

初回検証では、非公開モデルや購入素材を追加せずに起動してください。

シーンの再生成は既存ファイルを上書きする前に確認し、
`Library/GuardianSceneBackups`へ復元用コピーを作ります。ほかの編集中シーンと
Build Settingsの既存項目は保持されます。生成後のシーンには、従来と同じ
公開デモ用コード既定値が使われ、private設定は自動読込しません。

## 制御設定

通常起動は従来のデモ設定を維持します。公開サンプルを明示的に試す場合は、
Unityの起動引数へ `-guardianPublicSettings` を追加してください。

実運用設定は `.guardian/guardian-settings.private.json`（Git対象外）へ置くか、
`GUARDIAN_SETTINGS_PATH` / `-guardianSettingsPath <path>` で外部JSONを指定します。
公開サンプルは説明用であり、実機の安全限界や推奨値ではありません。
詳細と、既公開値がGit履歴から自動では消えない点は
[公開範囲と検証](docs/PUBLIC_SCOPE.md) を参照してください。

## 歩行者安全デモ

Physicsモードで公開用の歩行者横断デモを試す場合は、Unityの起動引数へ
`-guardianPedestrianDemo` を追加します。通常起動では無効なので、既存の
シナリオと検証結果は変わりません。

有効時は既存の自作スタッフモデルが通路を横断し、ロボットは予測距離と
物理的な障害物検出の両方で減速・停止します。歩行者通過後も公開設定の
クリア時間だけ停止を維持します。

この機能はUnity内の真値を直接使う審査用シミュレーションです。実カメラ、
実センサー、認証済み安全機能、実機の安全距離を表すものではありません。

## 日本語操作・会話デモ

Physicsモードで日本語パネルを表示する場合は、Unityの起動引数へ
`-guardianInteractionDemo` を追加します。通常起動では表示されません。

パネルでは `状況`、`A 状況`、`A 停止`、`A 復帰` のような定型入力を扱います。
停止・復帰は内容を表示したあと、別の `実行` 入力があるまで変更しません。
シナリオのリセット、対象状態の変化、15秒の確認期限切れがあれば拒否します。

これはローカルのルールベース文字UIです。マイク、音声認識、音声ファイル、
生成AI、外部API、ネットワーク通信は使用しません。日本語表示にはOFL-1.1の
Noto Sans JPを同梱し、OSの対応フォントが使えない場合のフォールバックにします。
出所とライセンスは [フォントREADME](Assets/Art/Fonts/README.md) を参照してください。

## 検証状況

GitHub Actionsでは設定ファイル、C#ソース、
Unityメタデータなどの静的検証を実施しています。

Unity Editorでのコンパイル、PlayMode、実際の描画は
別途確認が必要です。

ライセンス済みのUnity 6000.3.12f1では、Editorを閉じてから次の統合検証を実行できます。

- Windows: `./scripts/Invoke-GuardianVerification.ps1`
- macOS / Linux: `GUARDIAN_UNITY_PATH=/path/to/Unity ./scripts/verify-guardian-unity.sh`

設定境界、能力マトリクス、歩行者予測、日本語入力、公開シーン生成を1回の
バッチ起動で検証します。GitHub Actionsは実行入口とスクリプトを検証しますが、
Unityライセンスを使わないため、Actions成功だけでPlayMode・描画成功とは扱いません。

## 開発技術

- Unity / C#
- Blender / Python
- PowerShell / Bash
- GitHub Actions

## ライセンス

[LICENSE](LICENSE) を参照してください。

第三者のモデル・音声・画像等について、
本リポジトリのライセンスが再配布許可を与えるものではありません。

同梱するNoto Sans JPは、リポジトリのMITライセンスではなく
SIL Open Font License 1.1の条件で配布されます。

Nekobotの取込方法は
[こちら](Assets/Nekobot/README.md)を参照してください。
