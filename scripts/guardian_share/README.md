# 共有中継（PSoC 6 共有パネル ⇄ Bluesky）

Guardian が検知した異常を、人が PSoC 6 のボタンで承認したときだけ Bluesky に共有し、届いた返信を異常ごとのレポートにまとめる。ロボットとセンサーはインターネットにつながない。外へ出るのは、この PC から、人が承認した要約だけ。

```
StickS3 ──USB──▶ sensor_bridge.py ──TCP 127.0.0.1──▶ Unity
                       │ 記録 Logs/sensor_bridge/*.jsonl
                       ▼
PSoC 6 ──USB──▶ share_bridge.py ──HTTPS──▶ Bluesky（AT Protocol）
（SW2 で承認・ランプ）     │
                       ▼ 書き出し先（Google Drive for desktop の同期フォルダでもよい）
                  incidents/*.json, reports/*.md, reports/*.html
```

## 準備（初回だけ）

1. PSoC 6 に MicroPython と `main.py` を書き込む: [firmware/psoc6_share_panel](../../firmware/psoc6_share_panel/README.md)
2. センサー中継と同じ `.venv` に入れる:
   ```bash
   .venv/bin/pip install -r scripts/guardian_share/requirements.txt
   ```
3. Bluesky でアプリパスワードを作る（設定 → プライバシーとセキュリティ → アプリパスワード）。ログイン用の本パスワードは使わない
4. 環境変数で渡す。ファイルには書かない

   macOS:
   ```bash
   export BLUESKY_HANDLE=your-handle.bsky.social
   export BLUESKY_APP_PASSWORD=xxxx-xxxx-xxxx-xxxx
   ```
   Windows（PowerShell）:
   ```powershell
   $env:BLUESKY_HANDLE="your-handle.bsky.social"
   $env:BLUESKY_APP_PASSWORD="xxxx-xxxx-xxxx-xxxx"
   ```
   設定しないと試し運転になり、承認しても投稿しない。

## 使い方

1. いつもどおりセンサー中継を起動する
2. 別のウィンドウで共有中継を起動する
   ```bash
   .venv/bin/python scripts/guardian_share/share_bridge.py
   ```
3. センサー中継が注意・制限・保持にすると、PSoC 6 のランプがゆっくり点滅する。SW2 を押すと Bluesky に投稿する
4. 返信は 30 秒ごとに取りに行き、届くとランプが点灯して、レポートに書き足す。SW2 を押すと確認済みになる
5. 終わるときは Ctrl+C

| ランプ | 意味 | SW2 を押すと |
|---|---|---|
| ゆっくり点滅 | 共有待ちの異常がある | Bluesky に投稿する |
| 点灯 | 未確認の返信がある（投稿した直後の 1.5 秒も点灯） | 確認済みにする |
| 速い点滅（2 秒） | 投稿に失敗した | もう一度押すと試し直す |
| 消灯 | 何もない | 何もしない |

共有する前にさらに悪くなった場合（注意 → 制限）は、同じ異常の経過として 1 件にまとめる。起動する前からあった記録は、設定と測定値だけ読み、古い異常は共有待ちにしない。

### Google Drive に保存する

`--out-dir` に Google Drive for desktop の同期フォルダを指定する。ファイルは書き終えてから置き換えるので、書きかけが同期されることはない。

```bash
.venv/bin/python scripts/guardian_share/share_bridge.py --out-dir "$HOME/Library/CloudStorage/GoogleDrive-<アカウント>/マイドライブ/Guardian"
```
```powershell
.venv\Scripts\python scripts\guardian_share\share_bridge.py --out-dir "G:\マイドライブ\Guardian"
```

### リハーサル（PSoC 6 や StickS3 がないとき）

```bash
.venv/bin/python scripts/guardian_share/share_bridge.py --demo --no-psoc --keyboard --dry-run
```

模擬の異常を 1 件、共有待ちにして始まる。Enter キーを押すと承認する（`--dry-run` なので投稿はしない）。`--dry-run` を外すと、投稿文に「模擬データです」と書いて投稿する。

## 投稿する内容

```
【ロボもり 異常の共有 #001】
配膳ロボット B：制限（注意 → 制限）
理由：振動 300 mg 以上が 6 回続いたため制限（今回 398 mg）
直近の最大振動：415 mg（基準 300 mg）
いまの仕事：空いた皿の回収だけ（スープと料理は別の機体へ）
原因や点検のポイントに心当たりがあれば、返信で教えてください。
M5StickS3 で実測した値です（試作中のシステム）。
#ロボもり #JPHACKS
```

ロボットの記号と測定値だけを載せる。場所、機器の識別子、シリアルポート名などは載せない。

## 出力

| ファイル | 中身 |
|---|---|
| `incidents/incident-001.json` | 測定値、Guardian の判断と経過、投稿（URL と本文）、返信 |
| `reports/incident-001.md` / `.html` | 「実測データ」「Guardian の判断」「Bluesky での共有」「返信（外部の仮説）」「次に確かめること」に分けたレポート |

## 安全のための決まり

- 投稿は、人がボタンを押したときだけ。異常が出ても自動では投稿しない
- インターネットから入ってくるのは返信の文章だけ。外部の仮説としてレポートに載せるだけで、Guardian の判定や安全の設定は変えない
- 返信の文章は、HTML ではエスケープし、Markdown では字下げのコードブロックにする（画像やリンクを埋め込ませない）
- アプリパスワードは環境変数からだけ読み、記録にもファイルにも残さない

## テスト

```bash
python -m unittest discover -s scripts/guardian_share
```

Bluesky には接続しない（通信を偽物に差し替える）。PSoC 6 の `main.py` も、`machine` と MicroPython の `time` をまねた部品で動かして確かめる。偽の Bluesky サーバーと疑似シリアル上の `main.py` をつないだ通しの確認でも、異常 → 点滅 → SW2 → 投稿 → 返信 → レポートまで動いた。

**まだ確かめていないこと**: 実機の PSoC 6 での動作、本物の Bluesky への投稿。

## オプション

| オプション | 既定 | 内容 |
|---|---|---|
| `--sensor-log-dir` | `Logs/sensor_bridge` | センサー中継の記録の場所 |
| `--out-dir` | `Logs/guardian_share` | 書き出し先 |
| `--psoc-serial` | 自動（KitProg3 を探す） | PSoC 6 のシリアルポート |
| `--no-psoc` / `--keyboard` | | PSoC 6 を使わない / Enter キーでも承認する |
| `--dry-run` | | 承認しても投稿しない |
| `--demo` | | 模擬の異常を 1 件、共有待ちにして始める |
| `--hashtags` | `ロボもり,JPHACKS` | 投稿に付けるハッシュタグ |
| `--reply-interval` / `--follow-hours` | 30 秒 / 6 時間 | 返信を取りに行く間隔 / 投稿後に返信を追う時間 |
| `--service` / `--appview` | `https://bsky.social` / `https://public.api.bsky.app` | 投稿先の PDS / 返信を読む AppView |
