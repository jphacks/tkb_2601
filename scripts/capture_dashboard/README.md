# ダッシュボードの自動撮影

Robomori core のダッシュボードを Docker なしで立ち上げ、Unity の代わりの模擬プログラム（sim）でデータを流して、README 用のスクショを自動で撮ります。`docs/images/dashboard/` の画像は、これで撮ったものです。

```bash
PY=/path/to/venv/bin/python REPO=/path/to/tkb_2601 ./scripts/capture_dashboard/capture.sh
```

- `PY` は `core/requirements.txt` を入れた Python、`REPO` はこのリポジトリのルートです。
- Node 18 以上と、`require('playwright')` で読める Playwright（Chromium 入り）が要ります。
- 4 分半ほどかかります。画像は `scripts/capture_dashboard/shots/` にできます（Git には入れません）。

## していること

1. 作業用のフォルダを新しく作り、データベースの準備をする。
2. 「会社だけ（暗号化）」のデモ用の宛先を登録する（秘密鍵は捨てる）。今の時刻を中心に時間割を置く。
3. 監視 AI（`runhub`）、模擬プログラム（`simrobots`）、画面（gunicorn）を 127.0.0.1 で起動する。
4. ワンタイムパスワードを出して Playwright でログインし、監視 AI の減速と対応のおすすめが出るまで待つ。
5. 扉 2 を試し運転（Gemini・Bluesky には送らない）で 1 回開け、ルール AI に 2 つ質問してから撮影する。
6. 起動したものをすべて止める（Ctrl+C やエラーのときも）。

## 安全のために

- インターネットには出ません。Playwright は 127.0.0.1 とローカルのファイル以外への通信を止めます。Python の通信は使えないプロキシに向けます。
- ワンタイムパスワードは環境変数でだけ渡し、画面にもファイルにも書きません。Django の秘密鍵も実行ごとに作り、ファイルに残しません。
- 画像に写る鍵は公開鍵だけです。

設定できる環境変数（`WEB_PORT`、`HUB_PORT`、`TZ`、`CAPTURE_TIMEOUT`、`NO_TIMETABLE`、`NO_DOOR2`、`NO_VIEWER`、`KEEP_WORK`、`SHOTS`）は `capture.sh` の先頭に書いてあります。
