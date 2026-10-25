# ロボもり 中核（Robomori core / Docker）

システムモデルの「Robomori core（別管理）」の箱にあたる、Docker で動くバックエンドです。Django で記録と画面を持ち、監視 AI（`scripts/guardian_hub`）をそのまま動かします。

**本線には入りません。データを受け取るだけの別系統です。**

- StickS3 → センサー中継（127.0.0.1:47610）→ Unity の本線は、何も変えません。中核は 47610 を使いません。
- センサー中継の記録（`Logs/sensor_bridge/`）は、読み取り専用で読みます。中継には何も送りません。
- Unity のプログラムが落ちても、代役（sim）で同じ画面と監視 AI を見せられます。

```
 PC（Windows / Mac）                                   Docker（compose）
 Unity（GuardianHubLink）── 127.0.0.1:47612 ──▶ 門番 ──▶  core ネットワーク（internal: 外への道なし）
 ブラウザ ─────────────── 127.0.0.1:8000  ──▶ nginx ──▶  ├ hub   監視 AI・推薦・時間割・扉 1 の訓練
 ブラウザ ─ PC の IPv4:8000 ─▶ host-only の中継 ─▶ 127.0.0.1:8000（この PC 自身からだけ。合言葉で入る）
 センサー中継の記録 ─────────── 読み取り専用 ─────────▶  ├ web   Django（画面・API・管理画面）
                                                         ├ sim   Unity の代役（--profile sim）
                                                         └ SQLite（core-data ボリューム）
 PSoC 6 の SW2 ─▶ host/psoc6_key.py ─▶ docker compose run --rm door2
                                         door2net（internal: 外への道なし）
                                           door2 ──▶ egress（出口の中継）──▶ Gemini・Bluesky の 3 つの宛先だけ
 書き出しフォルダ（Logs/drive_out）◀── hub・door2 が要約を置く
        └─ host/psoc6_key.py --drive-folder が写す ─▶ Google Drive for desktop の同期フォルダ
```

| コンテナ | ネットワーク | 役目 |
|---|---|---|
| gateway（nginx） | edge・core | 127.0.0.1:8000 と 127.0.0.1:47612 だけを受け、中へ通す。外へは何もしない |
| web（Django + gunicorn） | core | 画面（合言葉で入る）、`/api/state`・`/api/layout`・`/api/drive`、PC 側の鍵の口 `/api/key`、管理画面（`/admin/`） |
| hub | core | Unity か代役から様子を受け、監視 AI の助言を返す。時間割に従って Unity を受けるか決め、「書き出し」の区間で要約を書き出しフォルダに置く |
| sim | core | Unity の代役。Unity と同じ形のデータを送り、助言も Unity と同じ決まりで受ける |
| door2 | door2net | 扉 2 が開いている間だけ存在する。Gemini の見立て、Bluesky の共有と返信、AT プロトコルの記録、書き出し |
| egress | door2net・egress | 扉 2 の出口。許可した 3 つの宛先の HTTPS だけを通す。30 秒使われなければ自分で終わる |
| migrate | core | 起動時に記録の表を用意して終わる |

画面の店内の地図は、**Unity のシーンにある物そのものから読みます**（`GuardianHubLink` が送る `scene`。テーブル・椅子・厨房・受け取り口・返却台・セルフレジ・案内板・扉・床の印・ロボットの待機場所・受け渡し場所を、カメラの検出のように「名前と床の上の枠」で表す）。Unity で家具を動かしたり足したりすると、つなぎ直したとき（または 5 秒以内）に画面も変わります。知らない種類の物も、その名前で出ます。画面の向きは Unity のカメラと同じで、北が上です。

`scripts/guardian_hub/layout.py` は Unity の配置の写しで、Unity の代役（sim）と、まだ Unity がつないでいないときの画面、推薦の経路（監視 AI の中の道のり）に使います。PV の地図（`pv/src/unityLayout.json`）も同じ値から書き出しています。

## 起動

**Windows では、PC に Python を入れません。** 要るのは Docker Desktop と、Windows に最初から入っている PowerShell だけです。Python を使う処理はすべてコンテナの中で動きます。USB の PSoC 6 は Docker Desktop のコンテナに渡せないので、PSoC 6 の鍵（`core/host/psoc6_key.ps1`）だけが PC 側の PowerShell で動きます。

1. 初回だけ、ネットがあるところでイメージを作る。会場でネットがなくても、作ったイメージで動きます。
   ```powershell
   cd core
   docker compose build
   docker compose pull gateway
   ```
2. 起動する（中核を立ち上げ、答えるまで待ってから、PSoC 6 の鍵と Google Drive へ写す処理を動かす）。
   ```powershell
   powershell -ExecutionPolicy Bypass -File core\start.ps1                    # Unity を使うとき
   powershell -ExecutionPolicy Bypass -File core\start.ps1 -Sim               # Unity の代役で見せるとき（Unity と同時には動かさない）
   powershell -ExecutionPolicy Bypass -File core\start.ps1 -DryRun            # 扉 2 を開けても Gemini・Bluesky には送らない
   powershell -ExecutionPolicy Bypass -File core\start.ps1 -Keyboard -NoPsoc  # PSoC 6 がないとき（Enter で扉 2 を開ける）
   ```
   Mac / Linux は `./core/start.sh`（`--sim`）。こちらは PC 側の鍵に Python（pyserial）を使います。
3. 画面は**ブラウザが自動で開き、ワンタイムパスワードで自動で入ります**。住所はこの PC の IPv4（例: `http://192.168.1.20:8000`。8000 番がふさがっていれば 8080〜8089 の空いている番号）。パスワードは `start.ps1` / `start.sh` の画面（ターミナル）にも出るので、手で入れることもできます（ブラウザを開かないときは `-NoBrowser`、Mac / Linux は `ROBOMORI_NO_BROWSER=1`）。
   あとから開き直すときは **`core\open-screen.cmd` をダブルクリック**します（新しいパスワードを出して、自動で入る）。
   この PC で動いているかを 1 回で確かめるときは、別の PowerShell で `powershell -ExecutionPolicy Bypass -File core\host\check_pc.ps1`（Mac / Linux は `python3 core/host/check_pc.py`）。画面と API が合言葉なしでは閉じていること、起動ごとの鍵で `/api/key` が読めること、ほかの PC から届かないこと（IPv4 の 8000 は host-only の中継）、中核が外へ出られないこと、扉 2 の出口と試し運転、PSoC 6、Google Drive を確かめて OK / 注意 / NG で出します（47612 にはつながないので、Unity の接続は押し出さない）。
4. Ctrl+C で止まるのは PSoC 6 の鍵だけで、中核は動いたままです。中核も止めるときは `cd core; docker compose --profile sim down`。記録は `core-data` ボリュームに残ります。

中核だけを動かすなら `cd core; docker compose up -d`（代役も: `--profile sim`）。中核が動いていなくても、止まっても、Unity の本線（StickS3 → センサー中継 → Unity）はそのまま動きます。Unity は 1 秒ごとにつなぎ直すだけです。

設定は `core/.env` に書きます（`.env.example` をコピー）。何も書かなくても動き、そのときは試し運転になります。発表では `HUB_LEARN_SECONDS=10`、`HUB_RELAX_AFTER=60` くらいが見せやすい値です。

管理画面（記録をそのまま見る）を使うときは、一度だけ利用者を作ります。

```bash
docker compose run --rm web python manage.py createsuperuser
```

## 画面に入る（ワンタイムパスワード・この PC からだけ）

画面とデータは、PC 側で出したワンタイムパスワードで入るまで、何も見えません（HTML・JavaScript・API・管理画面すべて）。

| しくみ | 中身 |
|---|---|
| 合言葉を出す | `start.ps1` / `start.sh` が起動のたびに 1 つ出して、その画面に表示し、ブラウザを `/login#code=…` で開いて自動で入る（`#` の後ろはサーバーに送られない。入口のスクリプトが読んで送り、すぐ住所と履歴から消す）。開き直すときは `core\open-screen.cmd`（= `core\host\otp.ps1`。手で入れるなら `-NoBrowser`）。Mac / Linux は `cd core && docker compose exec web python manage.py issue_otp`。`-LogoutAll`（`--logout-all`）で、入っている画面もすべて入り直しにする |
| 合言葉の形 | 8 文字（見間違えやすい 0・O・1・I・L は使わない）。1 回使うと使えない。10 分（`ROBOMORI_PASS_MINUTES`）たつか、5 回続けて間違えても使えない。新しく出すと前のものは取り消す |
| どこに残るか | 平文はどこにも残さない。DB には塩つきの scrypt のハッシュだけ。入口のページのソース（F12）にも、ヒントも含めて何も入れない |
| 入ったあと | セッションのクッキーは HttpOnly・SameSite=Strict（JavaScript からは読めない）。ブラウザを閉じるか 8 時間で入り直し。HTML と API は `Cache-Control: no-store`（戻るボタンやキャッシュに残さない）。「ログアウト」でその画面は入り直し |
| この PC からだけ | 中核（Docker）が受けるのは今までどおり `127.0.0.1` だけ。この PC の IPv4 では、PC 側の host-only の中継（Windows は `core\host\HostOnlyRelay.cs` を PowerShell がその場でコンパイル、Mac / Linux は `psoc6_key.py`）が受け、この PC 自身の住所からの接続だけを中核へ通す。ほかの PC からの接続は、何も読まずにすぐ切る（鍵の画面に「ほかの PC から…切った」と出る）。知らない名前（Host）で来たものは中核が 400 で断る |
| 起動ごとの鍵 | PSoC 6 の鍵と `check_pc` は、画面の合言葉ではなく、`start` が起動ごとに作る鍵（`Logs/.robomori-host-key`、コンテナには `ROBOMORI_HOST_KEY`）で `/api/key` から扉 2 の状態だけを読む |

Windows で初めて IPv4 の口を開けるとき、「Windows Defender ファイアウォールで…ブロックされています」と出ることがあります。**「キャンセル」（許可しない）でかまいません**。この PC 自身からの接続は、ファイアウォールを通らずに届きます。IPv4 の口を使わないときは `start.ps1 -NoListen`（Mac / Linux は `ROBOMORI_NO_LISTEN=1`）で、`127.0.0.1` だけにします。

IPv4 の 8000 番がふさがっているときは、`start.ps1` が何がふさいでいるかを出し、8080〜8089 の空いている番号で画面を出します（番号を決めるなら `-ListenPort 8080`）。ふさいでいるものが**全部の住所（0.0.0.0）で受けている**と赤字で警告します。そのときは `core\host\check_pc.ps1` を動かしてください（「受けている口」が NG なら、ほかの PC から中核に届くおそれがあります）。

## 画面（いまの店内を、Unity から）

画面構成図（`Main.dc.html`）の形で、すべて Unity（または代役）から届いた、いまの値だけを出します（「場面」はありません）。

| ところ | 中身 |
|---|---|
| ① 測る | Unity から届いた様子（ロボットの数・お客さんの数・受け取った時刻）、ロボットごとの振動指標の推移（点線が注意のしきい値）、センサー中継（M5StickS3）の段階、PSoC 6（人の鍵）の承認待ち |
| 接続ゲート | いまの接続（単独・店内・外部）、時間割、扉の記録 |
| 店内の地図 | Unity のシーンから読んだ配置に、0.5 秒ごとのロボット・お客さん（状態で色分け）・店員、椅子の空き具合（空席・片付け中・ご案内予定）、監視 AI が速度を下げた場所、対応のおすすめの道のりを重ねる |
| ② 判断する | ロボットごとに任せている仕事（スープ・料理・皿）と、**ロボット自身の説明**（Unity の `RobomoriTalk` が「今の状況は？」に答える文） |
| ③ 改善する | 監視 AI が自動で厳しくしたもの、人の承認を待つもの、対応のおすすめ |
| ロボットとのやり取り | Unity でロボットに聞いたこと（クリック・声・スマホ）と、ロボットの答え |
| 外への共有（AT プロトコル） | 扉 2 で何を出し、誰の返信を受け取るかを店が選ぶ。次に SW2 を押したら出る文と記録の中身、残した記録（下の「Bluesky」） |
| Google Drive の記録 | 右上のボタンで、右から受信箱が出る（下の「Google Drive」） |

ランプが黄色（注意）になっても、Unity のロボットは止まりません（`GuardianCapabilityPolicy.KeepServing`、既定でオン）。黄色のロボットは料理・皿の片付けを続け、スープだけ人の確認まで控えます。すでに別のロボットが注意か停止のときは、2 台目を同時に故障させません。料理はお客さんの席の前に置き、そこに食べ終わった皿があれば脇へ寄せてから置きます。記録つきの確かめ（`verifying`）では、今までどおりの止め方で動きます。

画面に出す配置・ロボットの説明・会話は、Unity 側の `GuardianHubLink` が送り、core は読んで出すだけです（Unity のコードに core の都合を入れない）。新しい家具は `RestaurantFixture` の種類で地図の記号が決まり、知らない種類も名前のまま出ます。

## 時間割（いつ、どこまでつなぐか）

人が前もって決めた時間割で、つながる範囲を切り替えます。切り替えるのは中核（hub）で、扉 2 は時間割が許す区間でも、人が SW2 を押したときにしか開きません。

| 区間 | Unity（扉 1） | 扉 2（SW2） | 外へ出るもの |
|---|---|---|---|
| standalone（単独） | 中核が受けない。Unity は単独で動く | 開かない | なし |
| store（店内・営業中） | つながる。監視 AI が助言する | 開かない | なし |
| share（店内・共有可） | つながる | SW2 で開けられる | SW2 を押したときだけ: Gemini・Bluesky・Drive |
| export（書き出し） | つながる | SW2 で開けられる | 区間の始めに 1 回だけ、要約を Drive 行きに置く（インターネットには出ない） |

時間割がない時刻は share として扱います。日をまたぐ区間も書けます。

```bash
docker compose exec web python manage.py timetable --set "00:00-09:00 standalone, 09:00-21:00 store, 21:00-21:10 export, 21:10-24:00 share"
docker compose exec web python manage.py timetable --demo --minutes 1   # 発表用: 今から 共有可 → 書き出し → 営業中 → 単独 → 共有可
docker compose exec web python manage.py timetable                      # 今の時間割を見る
```

時間割は、管理画面（`/admin/`）からも直せます。鍵は 2 か所でかけています。PC 側の `psoc6_key.py` が SW2 を受けても開けず（速い点滅）、`door2` コンテナ自身も時間割を確かめて開けません（終了コード 3）。

## 扉 2 を開ける（PSoC 6 の SW2）

PSoC 6 は USB の機器なので、Docker の外（PC 側）で動かします。ふつうは `start.ps1` / `start.sh` が起動します。Windows は PowerShell だけで動き（`core/host/psoc6_key.ps1`）、Mac / Linux は Python（pyserial）を使います（`core/host/psoc6_key.py`）。どちらも開くのは KitProg3（VID 04B4）の COM ポートだけで、StickS3 は開きません。

```powershell
powershell -ExecutionPolicy Bypass -File core\host\psoc6_key.ps1 -DriveFolder auto            # SW2 で扉 2 を開ける
powershell -ExecutionPolicy Bypass -File core\host\psoc6_key.ps1 -Keyboard -NoPsoc           # PSoC 6 がないとき（Enter で開ける）
powershell -ExecutionPolicy Bypass -File core\host\psoc6_key.ps1 -DryRun -DriveFolder auto    # 開けても Gemini・Bluesky には送らない
```

SW2 を押すと `docker compose run --rm door2` が 1 回だけ動きます。外とのやりとりが終わると扉 2 は閉じ、door2 コンテナと出口の中継（egress）も消えます。PSoC 6 のランプは次のとおりです。

| ランプ | 意味 |
|---|---|
| 消灯 | 外へ出す候補なし |
| ゆっくり点滅 | 外へ出す候補あり（監視 AI の判断や推薦が、まだ書き出されていない） |
| 点灯 | 扉 2 が開いている |
| 速い点滅（2 秒） | 開けられなかった（時間割で鍵）か、外とのやりとりでうまくいかないことがあった |

PSoC 6 に MicroPython と `main.py` がまだ入っていないと、鍵は「PSoC 6 のプログラムが答えない」と知らせます。Windows では `powershell -ExecutionPolicy Bypass -File core\host\psoc6_setup.ps1` で、Python を入れずに書き込めます（`firmware/psoc6_share_panel/README.md`）。

PSoC 6 なしで試すときは `docker compose run --rm door2` でも開けられます。

扉 2 で外に出すのは要約だけです（場所の名前、ロボットの記号、状態、倍率、推薦）。座標や生の測定値は出しません。Gemini の見立てと Bluesky の返信は「提案」として画面と記録に出すだけで、ロボットの動きは変えません。

## Google Drive

コンテナは Google の鍵を持ちません。要約（`guardian-records-日時.md` と `.jsonl`）を書き出しフォルダ（`EXPORT_FOLDER`、既定は `Logs/drive_out`）に置くだけです。Drive に送るのは PC の Google Drive for desktop で、書き出しフォルダから同期フォルダへ写すのは `psoc6_key.py` です。

1. PC に Google Drive for desktop を入れて、店のアカウントでログインする。
2. `start.ps1` / `start.sh` で起動する（Drive の同期フォルダを自動で探す）。マイドライブを自動で探し、その中の `robomori` フォルダ（なければ作る）へ写します。
   - Windows: `G:\マイドライブ` など（ストリーミング）、または `%USERPROFILE%\My Drive`（ミラーリング）
   - Mac: `~/Library/CloudStorage/GoogleDrive-<アカウント>/マイドライブ`
   - 見つからなければ、写さずに知らせます（`--drive-folder <フォルダ>` で指定もできる）。
3. つながっているか試す: `powershell -ExecutionPolicy Bypass -File core\host\psoc6_key.ps1 -DriveFolder auto -DriveCheck`（Mac / Linux は `python3 core/host/psoc6_key.py --drive-folder auto --drive-check`）。確認用のファイルを置き、たまった要約も写して終わります。drive.google.com の `robomori` フォルダに出れば、つながっています。

写すのは決まった名前の普通のファイルだけです（5 MB まで）。リンクは辿らないので、コンテナが書き出しフォルダにリンクを置いても、PC のほかのファイルが Drive に出ることはありません。書きかけが Drive に出ないよう、別名で書いてから置き換えます。外へ出す記録からは、IP アドレスとポート（コンテナの住所など）を消してあります。

### 画面の「Google Drive の記録」（受信箱）

右上の「Google Drive の記録」で、右から受信箱が開きます（Esc か × で閉じる）。書き出しフォルダの要約を 1 件ずつ「メール」として並べ、PC 側の鍵が Drive の同期フォルダへ写せたかを合わせて出します。

- フォルダ: すべて / Drive へ送った / まだ送っていない / うまくいかなかった / 時間割の書き出し / 扉 2（SW2 で開けた）/ 写しの記録（PC 側の 1 件ずつの結果）
- ディレクトリ: この PC の書き出しフォルダと、写す先の Drive の同期フォルダ
- 読む: 件名・時刻・ファイルごとの送った時刻（または失敗の理由）・Bluesky の投稿・要約の本文（文字のまま出す。HTML としては読まない）。未読は青い点（このブラウザにだけ覚える）

web は書き出しフォルダを**読むだけ**です（読み取り専用でつなぐ）。写した結果は、PC 側の鍵が書き出しフォルダの `.drive-log.jsonl` に 1 行ずつ書きます（この記録そのものは Drive へ写しません）。

## Bluesky（AT プロトコル）

扉 2 を開けたとき、減速や故障があれば、店のアカウントのリポジトリに**店の記録を 1 件**残します（独自の形 `internal.robomori.report`。定義は `core/lexicons/`）。店が選べば、同じ要約を Bluesky にも投稿します。次に扉 2 を開けたときは、前の投稿への返信を読みます。

記録は**店（運用者）のアカウントのもの**です。ログインは店のアカウントのアプリパスワードで、`.env` から door2 にだけ渡します。お客さんや返信した人のリポジトリには何も書きません（OAuth で本人のリポジトリに書く形は作っていません）。

| 記録の項目 | 中身 |
|---|---|
| `summary`・`slowdowns`・`responders`・`stopped` | 共有した要約、監視AI が速度を下げたところ（% の整数）、向かうとよい人（役割の名前）、止まっていたロボット。座標・生の測定値・人の名前は入れない。数は整数だけ（レコードに小数は入れられない）。文字は lexicon の長さ（UTF-8 のバイト数）に収める |
| `post` | 同じ内容の Bluesky の投稿（strongRef）。「記録だけ」を選んだときはない |
| `previous` | この店の 1 つ前の記録（strongRef）。記録を順にたどれ、途中が書き換えられたら分かる |
| `opinions` | 前の共有への返信のうち、店が受け取ると決めた人のもの。**文はコピーせず**、返信した人の DID と、その人のリポジトリにある投稿（strongRef）を指す。本人が消せば、どこからも読めなくなる |
| `share`・`accept` | この記録を出したときの店の選択（出し方、誰の返信を受け取っていたか）。記録自体が、どういう決まりで集めたかを名乗る |

### 会社だけに見せる（既定）

Bluesky（AT プロトコル）の投稿と記録は、すべて公開です（DM も運営からは読める）。そこで既定は **「会社だけ（暗号化）」** にしています。投稿はせず、店のリポジトリに **宛先の人の鍵でしか開けない記録** を 1 件置きます。Bluesky・PDS・ロボもりのサーバーには、中身は読めません。外から見えるのは、時刻・宛先の鍵の ID・前の記録への参照だけです。

1. 会社の人（宛先）: `core\viewer\index.html` を開き、「鍵を作る」→「公開の鍵をコピー」。秘密の鍵はそのブラウザの中だけにあり、取り出せません。Bluesky のアカウントは要りません。
2. 店: 画面の「外への共有」で「会社だけ（暗号化）」を選び、宛先に「名前 公開の鍵」を 1 行に 1 人書いて「この設定にする」。
3. SW2 で扉 2 を開けると、暗号化した記録が残る。宛先の人は、同じブラウザのビューアで店のハンドルを入れると、手元で開いて読める。

暗号の手順: 中身は 1024 バイト単位まで埋めて AES-256-GCM。宛先ごとに、使い捨ての ECDH P-256 と HKDF-SHA256 で包んだ鍵を付ける（`scripts/guardian_share/seal.py`、ビューアは WebCrypto）。書き込む先の PDS は `.env` の `BLUESKY_SERVICE` で変えられる（社内の PDS を立てたら、その住所を書く）。アプリパスワードは `.env` だけに置き、Git には入れない。

### 会社ごとの障害を、保守会社が見る（社内・VPN だけ）

目的: 会社ごとのロボットの障害を確かめ、障害が多い会社に修理の相談や買い替えを提案しやすくする。

- **会社ごとに分ける**: 会社 1 社 = AT プロトコルのアカウント（DID）1 つ。記録はその会社のリポジトリにだけ置く。
- **自分の会社の分しか見られない**: 記録は、その会社の人の鍵でしか開けない（上の「会社だけ」）。保守会社に見せる会社は、宛先に保守会社の鍵を 1 行足す（見せるかどうかは会社が決める）。
- **保守会社の一覧**: ビューアで会社のハンドルか DID を空白で区切って並べると、自分の鍵で開けた記録だけを数え、会社ごとに「危険・注意の件数、揺れの最大、KEY2 で戻した回数」と「修理の相談 / 入れ替え・買い替えの提案」を出す。鍵をもらっていない会社は件数しか分からない。
- **社内・VPN の中だけ**: ビューアの「社内 PDS の住所」に VPN の中の PDS を入れると、公開の窓口（plc.directory など）は使わず、その PDS だけを読む。扉 2 の書き込み先も `.env` の `BLUESKY_SERVICE`・`EGRESS_ALLOW`・`EGRESS_INTERNAL` で社内の PDS だけにする（出口の中継が社内の住所につなぐのは、`EGRESS_INTERNAL` に書いた名前だけ）。画面（中核）は、これまでどおりこの PC からだけ・ワンタイムパスワードで入る。

### デモを 1 回で動かす

```powershell
powershell -ExecutionPolicy Bypass -File core\host\demo.ps1                     # PSoC 6 を使う
powershell -ExecutionPolicy Bypass -File core\host\demo.ps1 -Keyboard -NoPsoc   # PSoC 6 がないとき（Enter で扉 2）
```

仮想 VPN の設定（なければ作る）→ 中核と社内 PDS の起動 → デモ用の宛先の鍵を作って「会社だけ（暗号化）」に登録 → M5 の中継を別の窓で起動 → ビューアを社内 PDS・A 社の状態で開く（10 秒ごとに読み直す）→ 中核の画面と PSoC 6 の鍵、の順に動く。扉 2 を開けるのは人（SW2 か Enter）のまま。Unity は自分で ▶ を押す。デモ用の宛先の鍵は `Logs\.robomori-demo-key.json`（Git に入れない）に置き、ビューアには住所の `#` の後ろで 1 回だけ渡す（読み込んだらすぐ消し、取り出せない形でそのブラウザに保存する）。本番では、宛先の人が自分のブラウザで鍵を作る。

**人や会社に結びつくものは、すべて `core\.env`（Git に入れない）に置く。** Bluesky のハンドルとアプリパスワード、仮想 VPN の会社のアカウント（`VPN_PDS_ACCOUNTS`。パスワードは `bluesky_setup.ps1 -Vpn` がランダムに作る。コードに既定のパスワードはなく、なければ社内 PDS は起動しない）、店の名前、記録の種類（`REPORT_COLLECTION`。空なら名前の入らない `internal.robomori.report`。公開の名前空間で使うときだけ、自分のドメインを逆にした名前を書く）。社内 PDS のログインの鍵は 2 時間で切れ、続けて 5 回間違えると 1 分は断る。

### 設定する（bsky.social か、仮想 VPN の社内 PDS）

```powershell
powershell -ExecutionPolicy Bypass -File core\host\bluesky_setup.ps1          # bsky.social の店のアカウント（ハンドルとアプリパスワードを聞く）
powershell -ExecutionPolicy Bypass -File core\host\bluesky_setup.ps1 -Vpn     # 仮想 VPN の社内 PDS（A 社。-Company b で B 社）
... -Write    # 確かめたあと、確認用の暗号化した記録を 1 件書く（画面で宛先を入れてから）
```

- アプリパスワードは画面に出さずに受け取り、`core\.env`（Git に入れない）にだけ書く。書いたあと、ログインと読み出しだけを確かめる（`python manage.py bsky_check`。投稿はしない）。
- **仮想 VPN**（`docker compose --profile vpn`。`start.ps1` は `.env` を見て一緒に起動する）: 外への道がない `vpn` ネットワークの中に、社内 PDS の代わり（`core/vpnpds/pds.py`。会社ごとのアカウント・記録の置き場所・読み出しの口だけ）を置く。扉 2 だけがこのネットワークでつながり、出口の中継は外のどこにも通さない。ブラウザ（ビューア）からは、この PC の `http://127.0.0.1:2583` からだけ届く。ビューアの「社内 PDS の住所」に `http://127.0.0.1:2583`、会社に `shop-a.vpn.internal` を入れて読む。
- 本物の社内 PDS や VPN（WireGuard など）に替えるときは、`.env` の `BLUESKY_SERVICE`・`BLUESKY_APPVIEW`・`EGRESS_ALLOW`・`EGRESS_INTERNAL` を、その PDS の住所に書き換える。

### 店が選ぶ（画面の「外への共有（AT プロトコル）」）

扉 2 を開けるのは、これまでどおり PSoC 6 の SW2 だけです。画面では、開けたときに何をするかを選びます。変えると監査ログに残ります。

| 選ぶもの | 選べる値 |
|---|---|
| 出すもの | 共有しない（投稿も記録も書かない）／記録だけ（タイムラインに流さず、記録だけ。記録は公開される）／投稿と記録 |
| Gemini | 要約を送って見立てを聞くか |
| 返信を受け取る相手 | 店がフォローしている人（既定）／指定した人（DID かハンドル。ハンドルは扉 2 で DID にしてから比べる）／だれでも |
| 受付期限 | 投稿から何日までの返信を受け取るか（1〜30 日、既定 7 日。AppView が受け取った時刻で見る） |

- 画面には「次に SW2 で扉 2 を開けたら」何が外に出るかを、投稿の文と記録の中身（JSON）のまま出します。
- 受け取る相手でない返信、期限を過ぎた返信は、数だけを記録し、文は画面にも記録にも Drive にも残しません。
- フォロー一覧を読めなかったときは、だれの返信も受け取りません（安全な側に倒す）。
- 返信は「提案」として出すだけで、ロボットの動きは変えません。

### ロボもりを使わずに読む（`core/viewer/index.html`）

`core\viewer\index.html` をダブルクリックしてブラウザで開き、店のハンドル（または DID）を入れます。ロボもりの中核（サーバー・API・データベース）は一切使わず、AT プロトコルの公開の窓口だけで読みます。

1. ハンドル → DID（`public.api.bsky.app` の resolveHandle）
2. DID → DID 文書（`plc.directory`、または did:web）。文書の側もそのハンドルを名乗っているか（双方向）を確かめる
3. DID 文書に書かれた店の PDS から、`com.atproto.repo.listRecords` で記録を読む
4. 意見は、返信した人それぞれの DID 文書から**その人の PDS** を探し、`getRecord` で読む
5. strongRef の CID と、いまの CID を比べる（「指したときのまま」「書き換わった」「消されている」）

記録の形は lexicon と同じ決まりで確かめ、合わないもの（小数入り、長すぎる、形が違う）は中身を出しません。外から来た文字は、すべて文字のまま出します（HTML として読まない）。ログインはしません。どの PC やスマホでも、同じファイルを開けば同じ記録が読めます（GitHub Pages などに置いても動きます）。

### 発表で見せる流れ（例）

1. 画面で「投稿と記録」「店がフォローしている人」を選び、「次に SW2 で扉 2 を開けたら」に出る文を見せる
2. SW2 を押す → Bluesky に投稿し、記録 1 が店のリポジトリに残る
3. 店がフォローしているアカウントと、していないアカウントから、それぞれ返信する
4. もう一度 SW2 を押す → フォローしている人の返信だけを受け取り、記録 2 がその返信（DID）と記録 1 を指す。もう一方は「取り込まなかった 1 件」とだけ残る
5. 別の PC かスマホで `core/viewer/index.html` を開き、店のハンドルを入れる → 記録 2 → 記録 1 とたどれ、意見は返信した人の PDS から読まれる
6. 返信した人が自分の返信を消す → ビューアで「意見: 消されている」になる（その人のデータは、その人が決める）

| 審査の観点 | ロボもりで見せられること |
|---|---|
| アカウントやデータを持ち運べる・ひらかれた仕組み | 記録は店の DID のリポジトリにあり、PDS を移しても同じ DID で読める。ロボもりを使わない別のクライアント（ビューア）が、公開の窓口だけで読める |
| 人と人のつながり | 返信した人の意見を、その人の DID と投稿への参照で記録に残す（店の記録から、意見をくれた人へたどれる） |
| 利用者が選べる・安心 | 出すもの・Gemini・受け取る相手・期限を店が選び、出る前に中身を見られる。開けるのは人の SW2 だけ。返信した人は自分の投稿を消せば、記録からも読めなくなる |

まだしていないこと: 本物の Bluesky との送受信（開発中は送らない決まり。偽の Bluesky と、偽の PLC・PDS で確かめた）、PDS の引っ越しそのもの、OAuth、Jetstream、フィード。

## 安全の作り

| 何を | どうしているか |
|---|---|
| インターネットに出られるもの | 出口の中継（egress）だけ。通すのは `generativelanguage.googleapis.com`・`bsky.social`・`public.api.bsky.app` の HTTPS（443）だけで、IP アドレスの直書き、ほかの名前、普通の HTTP、内側の住所に向く名前は断る。中身は TLS のまま流すだけで読まない |
| door2 | 外への道がない door2net にいて、出口の中継を通してしか外へ出られない。扉 2 が開いている間しか存在しない |
| web・hub・sim | 外への道がない core ネットワークだけ。外の名前も引けない（実際に確かめた） |
| 外から入れる口 | 127.0.0.1 の 8000 と 47612 だけ。ほかの PC からは届かない |
| 扉 2 を開けられる人 | PSoC 6 の SW2 か、PC で `docker compose run` を打った人だけ。画面からは開けない。時間割の鍵は PC 側と door2 の 2 か所。Docker の操作口（docker.sock）はどのコンテナにも渡していない |
| ネットワークからの指示 | 速度を下げることしかできない（Unity と代役の両方で、上げる指示は捨てる）。使えない指示が 10 秒に 3 回届いたら、扉 1 が自動で閉じる。時間割が「単独」の間は、中核が Unity を受けない |
| Google Drive | コンテナは Google の鍵を持たない。PC 側が、決まった名前の普通のファイルだけを写す（リンクは辿らない） |
| コンテナの権限 | 管理者でない利用者で動かし、ファイルは読み取り専用、権限はすべて外し、権限を上げることも禁止 |
| 画面 | 外の読み込みを許さない CSP、埋め込み禁止、CSRF の確認、知らないホスト名は拒否。外から来た文章は HTML にしない |
| 秘密の鍵 | Django の鍵は初回に `core-data` の中で作る。API キーとアプリパスワードは `.env`（Git に入れない）から door2 にだけ渡す。出口の中継からは見えない |
| 記録 | 監査ログは足すだけで、管理画面からも直せない・消せない |

## 気をつけること

- **Unity と代役は同時に動かさない。** hub が受ける接続は 1 本だけです。
- **`scripts/guardian_hub/hub.py`（Docker なしのハブ）とも同時に動かさない。** どちらも 127.0.0.1:47612 を使います。Docker が動かないときは、そちらに切り替えます。
- 書き出しフォルダ（`EXPORT_FOLDER`）は、Docker Desktop の「File sharing」に含まれている場所にします（リポジトリの中なら、ふつうは含まれます）。
- Linux で書き出しフォルダに書けないときは、`.env` に `DOOR2_USER=1000:1000`（`id -u`:`id -g`）を書き、フォルダを `chmod 777` にします（hub も書くため）。

## テスト

```bash
docker compose run --rm --no-deps --entrypoint python web manage.py test guardian   # 中核（56 件）
python -m unittest discover -s scripts/guardian_hub                                    # 監視ハブと扉 2 の中身（44 件）
python -m unittest discover -s scripts/guardian_share                                  # Bluesky の部品（33 件）
python -m unittest discover -s core/host                                               # PSoC 6 の鍵と Drive へ写す処理（16 件）
python -m unittest discover -s core/egress                                             # 扉 2 の出口（7 件）
```

確かめたこと:
- この環境で実際に `docker compose` を起動し、代役 → hub → 画面、門番経由の 47612、画面からの扉 1 の訓練、扉 2（試し運転）、センサー中継の記録の読み取りを確かめた。
- web と hub のコンテナから外（1.1.1.1、pypi.org）へつながらず、外の名前も引けないことを確かめた。
- 扉 2 の出口: door2 から `bsky.social` と Gemini には中継を通して届き、`example.com`・`pypi.org` は中継が断り（403）、中継を通さない接続（名前でも 1.1.1.1 でも）は届かないことを確かめた。中継は 30 秒使われないと自分で終わり、次の door2 でまた立ち上がる。
- 時間割: `timetable --demo` で 共有可 → 書き出し → 営業中 → 単独 → 共有可 を実際に回し、SW2 の代わりの Enter で、共有可では扉 2 が開いて閉じ、営業中と単独では開かず、単独では Unity の代役の接続を中核が受けないこと、書き出しは区間ごとに 1 回で、Drive の同期フォルダ（の代わりのフォルダ）へ写ることを確かめた。
- PSoC 6 は、実際の `main.py` を仮想のシリアルポートで動かし、`start.sh` で中核を立ち上げたうえで、SW2 → door2 コンテナ → 扉 2 が閉じる → 要約が Drive の同期フォルダ（の代わり）へ写る、と、時間割が「営業中」のときは SW2 を押しても開かずランプが速く点滅する、までを確かめた。
- 書き出した要約（.md と .jsonl）を、実際の Google Drive のマイドライブの `robomori` フォルダに置き、大きさが PC 側と同じ（1 バイトも欠けない）ことを確かめた。
- 遅れ: ほかの処理が記録（SQLite）を 3 秒止めても、Unity への助言は 0.5 秒おきのまま届いた（直す前は 2.7 秒止まった）。Unity の行を扱う側は DB に触らないことを、テストでも確かめている。
- 画面の地図と代役の動きを Unity の配置と見比べた（テーブルは東の壁ぎわ、入口とセルフレジは南の壁、中央の通路は 1 台ずつ）。
- 合言葉: 入る前は HTML・JavaScript・API が見えない（302 / 401）、間違いは 5 回まで、1 回使うと使えない、期限切れ、クッキーが HttpOnly・SameSite=Strict、ページのソースに合言葉が入らない、を Docker の上でブラウザ（Chromium）から確かめた。
- host-only の中継: `HostOnlyRelay.cs` を C# 5 として組み立て、この PC 自身の住所からは通り、ほかの住所・IPv6 は断り、口が使われているときは起動しない、を実際のソケットで確かめた（`psoc6_key.py` の中継も同じ）。
- AT プロトコル: 店の選択（共有しない・記録だけ・投稿と記録、受け取る相手、期限）どおりに扉 2 が書く・書かない・受け取ることを、偽の Bluesky で確かめた。ビューアは Chromium で、偽の PLC・PDS を相手に、ハンドル → DID → PDS の読み取り、CID の照合（そのまま・書き換わった・消された）、記録の連鎖、作者の食い違いの拒否、小数入りの記録の拒否、外から来た `<script>` を文字のまま出すこと、スマホの幅を確かめた。扉 2 が実際に作る記録（長い日本語を含む）を、ビューアがそのまま読めることも確かめた。
- 画面の地図: 偽の Unity から `scene`（Unity にしかない鉢植えを足したもの）を送り、地図に鉢植え・椅子の空き具合・お客さん・ロボットの説明とやり取りが出ることを確かめた。Google Drive の受信箱は、書き出しフォルダと `.drive-log.jsonl` から一覧・読む・未読・Esc で閉じる、スマホの幅で崩れない、を確かめた。

まだ確かめていないこと:
- 実機の PSoC 6、Windows / Mac の Docker Desktop、Google Drive for desktop（Drive の同期フォルダの見つけ方と写し方は、テストと Linux の上でだけ確かめた）。
- `start.ps1`・`host/psoc6_key.ps1`・`host/check_pc.ps1`・`host/otp.ps1` そのもの（この環境で PowerShell を動かせない）。PowerShell の中で `HostOnlyRelay.cs` を組み立てるところも同じ。中から呼ぶ docker のコマンド（コンテナの中の確かめ、扉 2 の出口、試し運転）は、同じ形でこの環境で確かめた。
- Gemini と Bluesky への実際の送信（試し運転のみ。届くことまでは確かめた）。
- Unity エディタの中での動き（黄色のまま配膳を続ける・席の前に置く・`scene` を送る）。Unity のスクリプトは、Unity 6000.3.12 の UnityEngine の部品に対して組み立てられることまでを確かめた。
