# 段階的な開発履歴

審査時に、どの機能をどの差分で追加したか確認できるよう、公開PRを整理します。
日付や評価期間の扱いはGitHub上のコミット・PR時刻を正として確認してください。

| PR | 主な内容 | 状態 |
| --- | --- | --- |
| [#1](https://github.com/jphacks/tkb_2601/pull/1) | Unityプロジェクト基盤 | mainへ反映済み |
| [#2](https://github.com/jphacks/tkb_2601/pull/2) | 自作レストラン3D素材 | mainへ反映済み |
| [#3](https://github.com/jphacks/tkb_2601/pull/3) | Guardianシミュレーション実行系 | mainへ反映済み |
| [#4](https://github.com/jphacks/tkb_2601/pull/4) | Editorシーン生成 | mainへ反映済み |
| [#5](https://github.com/jphacks/tkb_2601/pull/5) | ローカルNekobot取込ツール | mainへ反映済み |
| [#6](https://github.com/jphacks/tkb_2601/pull/6) | 公開範囲と検証資料 | mainへ反映済み |
| [#7](https://github.com/jphacks/tkb_2601/pull/7) | 公開デモ設定と実運用設定の分離 | mainへ反映済み |
| [#8](https://github.com/jphacks/tkb_2601/pull/8) | opt-in歩行者安全デモ | mainへ反映済み |
| [#9](https://github.com/jphacks/tkb_2601/pull/9) | opt-in日本語操作デモ | mainへ反映済み |
| [#10](https://github.com/jphacks/tkb_2601/pull/10) | 安全なEditorシーン生成・検証 | mainへ反映済み |
| [#11](https://github.com/jphacks/tkb_2601/pull/11) | Unity統合検証とCIハーネス | レビュー中 |

各PRの本文には、既存挙動への影響、公開範囲、検証結果、残る手動確認を記録します。
GitHub Actionsの成功は静的検証と配線検証を示し、Unity PlayMode、描画、実機安全を
自動的に証明するものではありません。
