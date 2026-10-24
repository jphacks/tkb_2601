# ロボもり — Unity配膳シミュレーター

## 製品概要

配膳ロボットの状態管理、仕事の割り当て・引き継ぎ、
店内の3Dシミュレーションを扱うUnityプロジェクトです。

## 特長

- ロボットの状態に応じた仕事の可否判定
- 配膳タスクの割り当てと再割り当て
- ロボットの移動・交通制御
- Unity Editorによるレストランシーン生成
- ローカル3Dモデルの任意インポート

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
5. シーンがない場合は `Guardian > Create or Rebuild 3D Simulator Scene` を実行します。
6. `Assets/Scenes/GuardianRestaurant.unity` を開きます。
7. Playボタンでシミュレーションを開始します。

初回検証では、非公開モデルや購入素材を追加せずに起動してください。

## 検証状況

GitHub Actionsでは設定ファイル、C#ソース、
Unityメタデータなどの静的検証を実施しています。

Unity Editorでのコンパイル、PlayMode、実際の描画は
別途確認が必要です。

## 開発技術

- Unity / C#
- Blender / Python
- PowerShell / Bash
- GitHub Actions

## ライセンス

[LICENSE](LICENSE) を参照してください。

第三者のモデル・音声・画像等について、
本リポジトリのライセンスが再配布許可を与えるものではありません。

Nekobotの取込方法は
[こちら](Assets/Nekobot/README.md)を参照してください。
