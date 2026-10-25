<#
デモを 1 回で動かす（Windows）: 仮想 VPN の社内 PDS・宛先の鍵の登録・M5 の中継・ビューア・中核を、順に立ち上げる。
扉 2 を開けるのは、これまでどおり人（PSoC 6 の SW2、-Keyboard なら Enter）。外へ出すかどうかは人が決める。

  powershell -ExecutionPolicy Bypass -File core\host\demo.ps1                     PSoC 6 を使う
  powershell -ExecutionPolicy Bypass -File core\host\demo.ps1 -Keyboard -NoPsoc   PSoC 6 がないとき（Enter で扉 2）
  ... -NoRelay    M5 の中継を起動しない      ... -NoViewer   ビューアを開かない
  ... -StageTest  段階の確かめ。先に Unity なしで 正常→注意→制限→KEY2で正常 を自動で確かめ、そのあと中継は StickS3 の
                  代わりに同じ揺れを繰り返し流す（Unity の B が 3 段階を巡るのを目で見る）。StickS3 本体には何もしない

デモ用の宛先の鍵は Logs\.robomori-demo-key.json（Git に入れない）に置き、ビューアには 1 回だけ住所の # の後ろで渡す
（ビューアは読み込んだらすぐ住所と履歴から消し、取り出せない形でそのブラウザに保存する）。本番では、宛先の人が
自分のブラウザで鍵を作る。Unity は自分で ▶ を押す。
#>
param(
  [switch]$Keyboard,
  [switch]$NoPsoc,
  [switch]$NoRelay,
  [switch]$NoViewer,
  [switch]$StageTest
)

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Core = Join-Path $Root 'core'
$Compose = Join-Path $Core 'docker-compose.yml'
$EnvFile = Join-Path $Core '.env'
$Logs = Join-Path $Root 'Logs'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
function Say([string]$Message) { Write-Host ('{0:HH:mm:ss} {1}' -f (Get-Date), $Message) }

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Say 'docker がない。Docker Desktop を起動してから'; exit 1 }

# 1. 仮想 VPN の設定（なければ組む。会社のアカウントはランダムなパスワードで .env にだけ書く）
if (-not ((Test-Path -LiteralPath $EnvFile) -and (Select-String -LiteralPath $EnvFile -Pattern '^BLUESKY_SERVICE=http://vpn-pds' -Quiet))) {
  & (Join-Path $PSScriptRoot 'bluesky_setup.ps1') -Vpn -NoCheck
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# 2. 社内 PDS を立ち上げ、デモ用の宛先の鍵を作って「会社だけ（暗号化）」に登録する。
#    中核そのものは、最後に start.ps1 が 1 回だけ起動する（ここで起動すると、起動ごとの鍵が変わって 2 回作り直すため遅い）
# 中核のイメージ（robomori-core:local）にはコードが入っている。git pull のあとは作り直さないと古いまま動くので、
# 毎回作る（変わっていなければ数秒。初めての PC では数分かかり、ネットが要る）
Say '中核のイメージを最新のコードで作る（変わっていなければ数秒）'
& docker compose -f $Compose build web
if ($LASTEXITCODE -ne 0) { Say 'イメージを作れなかった。上のエラーを見る（ネットにつながっているか）'; exit 1 }
Say '仮想 VPN の社内 PDS を起動し、宛先を登録する'
& docker compose -f $Compose --profile vpn up -d --force-recreate vpn-pds
if ($LASTEXITCODE -ne 0) { Say '起動できなかった。cd core; docker compose build を先に'; exit 1 }
# 社内 PDS が本当に答えるまで待つ（ビューアが「読めなかった」にならないように）
$pdsOk = $false
for ($i = 0; $i -lt 30 -and -not $pdsOk; $i++) {
  try {
    $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 -Uri 'http://127.0.0.1:2583/xrpc/com.atproto.server.describeServer'
    $pdsOk = $r.StatusCode -eq 200
  } catch { Start-Sleep -Milliseconds 500 }
}
if ($pdsOk) { Say '社内 PDS が答えた（http://127.0.0.1:2583）' }
else {
  & docker compose -f $Compose --profile vpn logs --tail 15 vpn-pds
  Say '社内 PDS が答えない（上のログを見る。VPN_PDS_ACCOUNTS がない、など）'; exit 1
}
$migrate = @(& docker compose -f $Compose run --rm --no-deps web python manage.py migrate --noinput 2>&1)
if ($LASTEXITCODE -ne 0) { $migrate | Select-Object -Last 15 | ForEach-Object { Write-Host $_ }; Say 'データベースを準備できなかった（上のエラーを見る）'; exit 1 }
$output = @(& docker compose -f $Compose run --rm --no-deps web python manage.py demo_setup 2>&1 | ForEach-Object { "$_" })
$setupExit = $LASTEXITCODE
$jwk = ($output | Where-Object { $_ -match '^\{"kty"' } | Select-Object -Last 1)
if ($setupExit -ne 0 -or -not $jwk) { $output | Where-Object { $_ -notmatch '^\{"kty"' } | Select-Object -Last 15 | ForEach-Object { Write-Host $_ }; Say '宛先の鍵を登録できなかった（上のエラーを見る）'; exit 1 }
New-Item -ItemType Directory -Force -Path $Logs | Out-Null
[System.IO.File]::WriteAllText((Join-Path $Logs '.robomori-demo-key.json'), $jwk, (New-Object System.Text.UTF8Encoding $false))
Say ($output | Where-Object { $_ -notmatch '^\{' } | Select-Object -Last 1)

# 3. M5 の中継を別の窓で（StickS3 を USB でつないでおく）
$python = Join-Path $Root '.venv\Scripts\python.exe'
if ($StageTest) {
  if (-not (Test-Path -LiteralPath $python)) { Say '段階の確かめには .venv が要る（README の「センサー中継」を参照）'; exit 1 }
  Say '段階の確かめ（Unity なし、約 10 秒）: 正常 → 注意 → 制限 → KEY2 で正常'
  & $python (Join-Path $Root 'scripts\sensor_bridge\check_stages.py')
  if ($LASTEXITCODE -ne 0) { Say '段階の確かめに失敗した。上の NG を見る'; exit 1 }
}
$relayRunning = $false
try {   # .NET で直接（Get-NetTCPConnection は数秒かかることがある）
  $relayRunning = @([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
    Where-Object { $_.Port -eq 47610 -or $_.Port -eq 18610 }).Count -gt 0
} catch { }
if ($relayRunning -and -not $NoRelay) {
  if ($StageTest) { Say 'M5 の中継がもう動いている。段階の確かめでは作った揺れを流す中継を使うので、その窓を閉じてからもう一度'; exit 1 }
  Say 'M5 の中継はもう動いている（47610 か 18610 を受けている）ので、新しくは起動しない'; $NoRelay = $true
}
if (-not $NoRelay) {
  if (Test-Path -LiteralPath $python) {
    $relayArgs = if ($StageTest) { ' --simulate --loop' } else { '' }
    Start-Process powershell -ArgumentList @('-NoExit', '-Command', "Set-Location -LiteralPath '$Root'; & '$python' scripts\sensor_bridge\sensor_bridge.py$relayArgs")
    if ($StageTest) { Say '中継を別の窓で起動した（段階の確かめ: 作った揺れを繰り返し流す。Unity の B が 正常→注意→制限→正常 を巡る）' }
    else { Say 'M5 の中継を別の窓で起動した' }
  } else { Say 'M5 の中継は起動していない（.venv がない。README の「センサー中継」を参照）' }
}

# 4. ビューア: 社内 PDS と A 社を入れた状態で開き、10 秒ごとに読み直す。デモ用の鍵は # の後ろで 1 回だけ渡す
if (-not $NoViewer) {
  $key = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($jwk)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
  $page = 'file:///' + ((Join-Path $Core 'viewer\index.html') -replace '\\', '/')
  $url = $page + '?pds=' + [Uri]::EscapeDataString('http://127.0.0.1:2583') + '&actor=shop-a.vpn.internal&watch=1#key=' + $key
  $opened = $false
  foreach ($browser in 'msedge', 'chrome') {
    if (-not $opened) { try { Start-Process $browser -ArgumentList @($url) -ErrorAction Stop; $opened = $true } catch { } }
  }
  if ($opened) { Say 'ビューアを開いた（社内 PDS の A 社を 10 秒ごとに読み直す。デモ用の鍵を読み込んだ）' }
  else { Say ('ビューアを開けなかった。core\viewer\index.html を開き、社内 PDS に http://127.0.0.1:2583 を入れる') }
}

Say 'Unity で ▶ を押す。StickS3 を揺らして B を注意以上にし、SW2（-Keyboard なら Enter）で扉 2 を開けると、記録がビューアに出る'

# 5. 中核の画面と PSoC 6 の鍵（ここから先は start.ps1 と同じ。Ctrl+C で鍵だけ止まる）
$start = @{}
if ($Keyboard) { $start.Keyboard = $true }
if ($NoPsoc) { $start.NoPsoc = $true }
& (Join-Path $Core 'start.ps1') @start
exit $LASTEXITCODE
