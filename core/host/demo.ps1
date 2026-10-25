<#
デモを 1 回で動かす（Windows）: 仮想 VPN の社内 PDS・宛先の鍵の登録・M5 の中継・ビューア・中核を、順に立ち上げる。
扉 2 を開けるのは、これまでどおり人（PSoC 6 の SW2、-Keyboard なら Enter）。外へ出すかどうかは人が決める。

  powershell -ExecutionPolicy Bypass -File core\host\demo.ps1                     PSoC 6 を使う
  powershell -ExecutionPolicy Bypass -File core\host\demo.ps1 -Keyboard -NoPsoc   PSoC 6 がないとき（Enter で扉 2）
  ... -NoRelay    M5 の中継を起動しない      ... -NoViewer   ビューアを開かない

デモ用の宛先の鍵は Logs\.robomori-demo-key.json（Git に入れない）に置き、ビューアには 1 回だけ住所の # の後ろで渡す
（ビューアは読み込んだらすぐ住所と履歴から消し、取り出せない形でそのブラウザに保存する）。本番では、宛先の人が
自分のブラウザで鍵を作る。Unity は自分で ▶ を押す。
#>
param(
  [switch]$Keyboard,
  [switch]$NoPsoc,
  [switch]$NoRelay,
  [switch]$NoViewer
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
Say '仮想 VPN の社内 PDS を起動し、宛先を登録する'
& docker compose -f $Compose --profile vpn up -d vpn-pds
if ($LASTEXITCODE -ne 0) { Say '起動できなかった。cd core; docker compose build を先に'; exit 1 }
& docker compose -f $Compose run --rm --no-deps web python manage.py migrate --noinput *> $null
$output = @(& docker compose -f $Compose run --rm --no-deps web python manage.py demo_setup 2>$null)
$jwk = ($output | Where-Object { $_ -match '^\{"kty"' } | Select-Object -Last 1)
if (-not $jwk) { Say '宛先の鍵を登録できなかった'; exit 1 }
New-Item -ItemType Directory -Force -Path $Logs | Out-Null
[System.IO.File]::WriteAllText((Join-Path $Logs '.robomori-demo-key.json'), $jwk, (New-Object System.Text.UTF8Encoding $false))
Say ($output | Where-Object { $_ -notmatch '^\{' } | Select-Object -Last 1)

# 3. M5 の中継を別の窓で（StickS3 を USB でつないでおく）
$python = Join-Path $Root '.venv\Scripts\python.exe'
$relayRunning = $false
try {   # .NET で直接（Get-NetTCPConnection は数秒かかることがある）
  $relayRunning = @([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
    Where-Object { $_.Port -eq 47610 -or $_.Port -eq 18610 }).Count -gt 0
} catch { }
if ($relayRunning -and -not $NoRelay) { Say 'M5 の中継はもう動いている（47610 か 18610 を受けている）ので、新しくは起動しない'; $NoRelay = $true }
if (-not $NoRelay) {
  if (Test-Path -LiteralPath $python) {
    Start-Process powershell -ArgumentList @('-NoExit', '-Command', "Set-Location -LiteralPath '$Root'; & '$python' scripts\sensor_bridge\sensor_bridge.py")
    Say 'M5 の中継を別の窓で起動した'
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
