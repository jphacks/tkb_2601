<#
この PC で、ロボもり 中核がちゃんと動いているかを 1 回で確かめる（Windows。Python は要らない）。中核を起動してから使う。

  powershell -ExecutionPolicy Bypass -File core\host\check_pc.ps1
  ... -NoDoor2          扉 2 の出口と試し運転を確かめない
  ... -NoDriveWrite     Google Drive に確認用のファイルを置かない

PC 側で確かめるのは、画面と API・合言葉なしでは見られないこと・ほかの PC から届かないこと・PSoC 6・Google Drive。
コンテナの中のこと（外へ出られない、扉 2 の出口）は、コンテナの中の Python で確かめる。Unity の本線には触らず、47612 にも
つながない（Unity の接続を押し出すため）。画面の合言葉は出さない（出すと、いま使える合言葉が取り消されるため）。
中核の状態は、start.ps1 が起動ごとに作る鍵（Logs\.robomori-host-key）で /api/key から読む。
#>
param(
  [switch]$NoDoor2,
  [switch]$NoDriveWrite
)

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Compose = Join-Path $Root 'core\docker-compose.yml'
$Base = 'http://127.0.0.1:8000'
$KeyFile = Join-Path $Root 'Logs\.robomori-host-key'
$HostKey = if (Test-Path -LiteralPath $KeyFile) { ([System.IO.File]::ReadAllText($KeyFile)).Trim() } else { '' }
$OutputEncoding = New-Object System.Text.UTF8Encoding $false
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$script:results = @()

function Report([string]$Mark, [string]$Item, [string]$Detail) {
  $script:results += $Mark
  $color = @{ 'OK' = 'Green'; '注意' = 'Yellow'; 'NG' = 'Red' }[$Mark]
  Write-Host ('{0,-4} ' -f $Mark) -ForegroundColor $color -NoNewline
  Write-Host "${Item}: $Detail"
}

function Get-Status([string]$Url) {
  # HTTP の答えの番号だけ（転送はたどらない）。届かなければ 0
  try {
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Timeout = 3000; $request.Proxy = $null; $request.AllowAutoRedirect = $false
    $response = $request.GetResponse()
    try { return [int]$response.StatusCode } finally { $response.Close() }
  } catch {
    $e = $_.Exception
    while ($e -and -not ($e -is [System.Net.WebException])) { $e = $e.InnerException }
    if ($e -and $e.Response) { return [int]$e.Response.StatusCode }
    return 0
  }
}

function Get-Json([string]$Path) {
  $request = [System.Net.HttpWebRequest]::Create($Base + $Path)
  $request.Timeout = 3000
  $request.Proxy = $null
  if ($HostKey) { $request.Headers.Add('X-Robomori-Key', $HostKey) }
  $response = $request.GetResponse()
  try {
    $text = (New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)).ReadToEnd()
    if ($Path.StartsWith('/api')) { return ($text | ConvertFrom-Json) } else { return $text }
  } finally { $response.Close() }
}

function Invoke-Docker([string[]]$Arguments, [string]$Stdin = $null) {
  # docker の出力（stderr も）を文字列で返す。終了コードは $LASTEXITCODE
  if ($null -ne $Stdin) { $output = $Stdin | & docker @Arguments 2>&1 } else { $output = & docker @Arguments 2>&1 }
  return (($output | ForEach-Object { "$_" }) -join "`n")
}

function Check-Core {
  try {
    $null = Get-Json '/healthz'
  } catch {
    Report 'NG' '中核の画面' "$Base に届かない（$($_.Exception.Message)）。core\start.ps1 で起動してから"
    return $false
  }
  Report 'OK' '中核の画面' "$Base が答えた"
  $locked = @{ '/' = 302; '/api/state' = 401; '/api/layout' = 401; '/api/drive' = 401; '/static/guardian/dashboard.js' = 302 }
  $open = @($locked.Keys | Where-Object { (Get-Status ($Base + $_)) -ne $locked[$_] })
  if ($open.Count) { Report 'NG' '合言葉' ('合言葉なしで見えてしまう: ' + ($open -join ', ')) }
  else { Report 'OK' '合言葉' '画面・データ・画面のスクリプトは、ワンタイムパスワードで入るまで見えない' }
  if (-not $HostKey) { Report '注意' '起動ごとの鍵' 'Logs\.robomori-host-key がない。core\start.ps1 で起動すると、中核の状態まで確かめる'; return $true }
  try { $state = Get-Json '/api/key' } catch {
    Report 'NG' '起動ごとの鍵' "中核が鍵を受け付けない（$($_.Exception.Message)）。core\start.ps1 で起動し直す"
    return $true
  }
  $watch = [System.Diagnostics.Stopwatch]::StartNew()
  for ($i = 0; $i -lt 10; $i++) { $null = Get-Json '/api/key' }
  Report 'OK' '中核の速さ' ('/api/key 1 回 {0:N0} ms（10 回の平均）' -f ($watch.Elapsed.TotalMilliseconds / 10))
  if ($state.unity.connected) {
    if ($state.unity.stand_in) { Report 'OK' 'Unity（扉 1）' '代役（sim）とつながっている' } else { Report 'OK' 'Unity（扉 1）' 'Unity とつながっている' }
  } else {
    Report '注意' 'Unity（扉 1）' 'つながっていない。Unity を起動するか、core\start.ps1 -Sim で代役を使う'
  }
  $plan = $state.schedule
  $door2 = if ($plan.door2_allowed) { 'SW2 で開けられる' } else { '開かない' }
  Report 'OK' '時間割' ("いま「{0}」。扉 2 は {1}" -f $plan.label, $door2)
  $relay = @{ 'fresh' = @('OK', '測定値が届いている'); 'stale' = @('注意', '途切れている'); 'none' = @('注意', '記録なし（センサー中継が動いていない）') }[$state.relay]
  Report $relay[0] 'センサー中継の記録' $relay[1]
  return $true
}

function Get-LanAddress {
  try {
    $probe = New-Object System.Net.Sockets.Socket([System.Net.Sockets.AddressFamily]::InterNetwork, [System.Net.Sockets.SocketType]::Dgram, [System.Net.Sockets.ProtocolType]::Udp)
    try { $probe.Connect('192.0.2.1', 9); $address = $probe.LocalEndPoint.Address.ToString() } finally { $probe.Dispose() }   # UDP の connect は何も送らない
    if ($address.StartsWith('127.')) { return $null }
    return $address
  } catch { return $null }
}

function Get-ScreenPort {
  # start.ps1 が画面を出した port（8000 がふさがっていると 8080 など）。Logs\.robomori-screen に書いてある
  $file = Join-Path $Root 'Logs\.robomori-screen'
  if (Test-Path -LiteralPath $file) {
    $saved = ([System.IO.File]::ReadAllText($file)).Trim()
    if ($saved -match '^http://[0-9.]+:([0-9]{2,5})$' -and $saved -notmatch '^http://127\.') { return [int]$Matches[1] }
  }
  return 8000
}

function Check-Listeners {
  # 8000（画面）と 47612（Unity）を、全部の住所（0.0.0.0 / ::）で受けているものがあれば、ほかの PC から届くおそれがある
  try { $rows = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $_.LocalPort -eq 8000 -or $_.LocalPort -eq 47612 }) }
  catch { Report '注意' '受けている口' 'Get-NetTCPConnection が使えないので、確かめていない'; return }
  $open = @($rows | Where-Object { $_.LocalAddress -eq '0.0.0.0' -or $_.LocalAddress -eq '::' })
  foreach ($row in $open) {
    $name = '?'
    try { $name = (Get-Process -Id $row.OwningProcess -ErrorAction Stop).ProcessName } catch { }
    Report 'NG' '受けている口' ("{0}（pid {1}）が {2}:{3} を全部の住所で受けている。ほかの PC から中核に届くおそれ" -f $name, $row.OwningProcess, $row.LocalAddress, $row.LocalPort)
  }
  if (-not $open.Count) { Report 'OK' '受けている口' '8000・47612 を全部の住所で受けているものはない（127.0.0.1 と、この PC の住所の中継だけ）' }
}

function Check-Lan {
  $address = Get-LanAddress
  if (-not $address) { Report '注意' 'ほかの PC から' 'LAN の住所が分からないので、確かめていない'; return }
  $client = New-Object System.Net.Sockets.TcpClient
  $hub = $false
  try { $hub = $client.ConnectAsync($address, 47612).Wait(1000) } catch { } finally { $client.Dispose() }
  if ($hub) { Report 'NG' 'ほかの PC から' ("{0}:47612 に届いてしまう。docker-compose.yml の ports が 127.0.0.1 になっているか確かめる" -f $address) }
  else { Report 'OK' 'ほかの PC から' "$address の 47612（Unity の口）には届かない" }
  # 画面の port は、start.ps1 が出す host-only の中継（この PC からの接続だけを中核へ通す）が受けていてよい
  $port = Get-ScreenPort
  $screen = Get-Status ("http://{0}:{1}/healthz" -f $address, $port)
  if ($screen -eq 200) { Report 'OK' '画面の住所' ("http://{0}:{1} は host-only の中継（この PC からだけ。ほかの PC からはすぐ切る。合言葉も要る）" -f $address, $port) }
  elseif ($screen -eq 0) { Report 'OK' '画面の住所' ("{0}:{1} には出していない（画面は http://127.0.0.1:8000）" -f $address, $port) }
  else { Report '注意' '画面の住所' ("{0}:{1} が思わぬ答え（{2}）" -f $address, $port, $screen) }
  if ($port -ne 8000 -and (Get-Status ("http://{0}:8000/healthz" -f $address)) -eq 200) {
    Report 'NG' '画面の住所' ("{0}:8000 でも中核が答える。中継ではないもの（Docker など）が全部の住所で受けているおそれ" -f $address)
  }
}

function Check-CoreOffline {
  $code = @'
import socket
for target in (("1.1.1.1", 443), ("example.com", 443)):
    try:
        socket.create_connection(target, 3).close()
        print("reached", target[0])
    except OSError:
        pass
print("done")
'@
  $output = Invoke-Docker @('compose', '-f', $Compose, 'exec', '-T', 'web', 'python', '-') $code
  if ($output -match 'reached') { Report 'NG' '中核から外へ' ('web コンテナから外に届いてしまう: ' + $output) }
  elseif ($output -match 'done') { Report 'OK' '中核から外へ' 'web コンテナから 1.1.1.1 にも example.com にも届かない（名前も引けない）' }
  else { Report '注意' '中核から外へ' ('確かめられなかった: ' + $output.Substring(0, [math]::Min(160, $output.Length))) }
}

function Check-Egress {
  $code = @'
import urllib.error, urllib.request
def go(url, proxy=True):
    opener = urllib.request.build_opener() if proxy else urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(url, timeout=15):
            return "ok"
    except urllib.error.HTTPError as error:
        return "ok" if error.code < 500 else "ng"
    except Exception as error:
        return "refused" if "403" in str(error) else "ng"
print("allowed", go("https://bsky.social/xrpc/_health"))
print("other", go("https://example.com/"))
print("direct", go("https://bsky.social/xrpc/_health", proxy=False))
'@
  try {
    $output = Invoke-Docker @('compose', '-f', $Compose, 'run', '--rm', '-T', '--entrypoint', 'python', 'door2', '-') $code
  } finally {
    $null = & docker compose -f $Compose --profile door2 rm --stop --force egress 2>&1
  }
  $lines = @{}
  foreach ($line in ($output -split "`n")) { if ($line -match '^(allowed|other|direct) (\w+)$') { $lines[$Matches[1]] = $Matches[2] } }
  if ($lines['other'] -ne 'refused' -or $lines['direct'] -eq 'ok') {
    Report 'NG' '扉 2 の出口' ('許可していない宛先に届いた可能性がある: ' + $output.Substring(0, [math]::Min(200, $output.Length)))
  } elseif ($lines['allowed'] -eq 'ok') {
    Report 'OK' '扉 2 の出口' 'bsky.social には中継を通して届き、example.com は断り、中継を通さない接続は届かない'
  } else {
    Report '注意' '扉 2 の出口' '許可していない宛先は断った。bsky.social にも届かない（この PC がインターネットにつながっていないなら正常）'
  }
}

function Check-Door2 {
  try {
    $output = Invoke-Docker @('compose', '-f', $Compose, 'run', '--rm', '-T', '-e', 'DOOR2_DRY_RUN=1', '-e', 'OPENED_BY=check_pc.ps1', 'door2')
    $code = $LASTEXITCODE
  } finally {
    $null = & docker compose -f $Compose --profile door2 rm --stop --force egress 2>&1
  }
  $last = (($output -split "`n") | Where-Object { $_.Trim() -and $_ -notmatch 'Container|Network' } | Select-Object -Last 1)
  if ($code -eq 0) { Report 'OK' '扉 2 の試し運転' $last }
  elseif ($code -eq 3) { Report 'OK' '扉 2 の試し運転' '時間割で鍵がかかっていたので開けなかった（正しい動き）' }
  else { Report 'NG' '扉 2 の試し運転' "終了コード ${code}: $last" }
}

function Check-Psoc {
  try { $devices = @(Get-CimInstance -ClassName Win32_PnPEntity -ErrorAction Stop | Where-Object { $_.PNPDeviceID -match '^USB\\VID_(04B4|303A)' -and $_.Name -match '\((COM\d+)\)' }) }
  catch { Report '注意' 'PSoC 6' 'USB の機器を調べられなかった'; return }
  $psoc = @($devices | Where-Object { $_.PNPDeviceID -match 'VID_04B4' } | ForEach-Object { if ($_.Name -match '\((COM\d+)\)') { $Matches[1] } })
  $sticks = @($devices | Where-Object { $_.PNPDeviceID -match 'VID_303A' } | ForEach-Object { if ($_.Name -match '\((COM\d+)\)') { $Matches[1] } })
  $note = if ($sticks.Count) { "（StickS3 は $($sticks -join ', ') に見えている。開かない）" } else { '' }
  if ($psoc.Count) { Report 'OK' 'PSoC 6' "KitProg3 が $($psoc -join ', ') に見えている$note" }
  else { Report '注意' 'PSoC 6' "KitProg3 が見えない。KitProg3 側の USB をつなぐ$note" }
}

function Check-Drive {
  $roots = @()
  foreach ($letter in 'GHIJKLMNOPQRSTUVWXYZDEF'.ToCharArray()) { foreach ($name in @('マイドライブ', 'My Drive')) { $roots += ('{0}:\{1}' -f $letter, $name) } }
  foreach ($name in @('マイドライブ', 'My Drive')) { $roots += (Join-Path $HOME $name) }
  $root = $roots | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
  if (-not $root) { Report '注意' 'Google Drive' 'Google Drive for desktop が見つからない。入れて店のアカウントでログインする'; return }
  $folder = Join-Path $root 'robomori'
  if ($NoDriveWrite) { Report 'OK' 'Google Drive' "同期フォルダ: $folder"; return }
  try {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { New-Item -ItemType Directory -Path $folder -ErrorAction Stop | Out-Null }
    $path = Join-Path $folder ('robomori-drive-check-{0:yyyyMMdd-HHmmss}.txt' -f (Get-Date))
    [System.IO.File]::WriteAllText($path, ('ロボもり: Google Drive へ写せるかの確認（{0:yyyy-MM-dd HH:mm:ss}）' -f (Get-Date)) + "`n", (New-Object System.Text.UTF8Encoding $false))
    Report 'OK' 'Google Drive' "$path を置いた。drive.google.com の robomori フォルダに出れば届いている"
  } catch {
    Report 'NG' 'Google Drive' "$folder に書けない（$($_.Exception.Message)）"
  }
}

$null = & docker compose version 2>&1
if ($LASTEXITCODE -ne 0) { Report 'NG' 'Docker' 'docker compose が動かない。Docker Desktop を起動する'; exit 1 }
Report 'OK' 'Docker' 'docker compose が動く'
if (Check-Core) {
  Check-Lan
  Check-Listeners
  Check-CoreOffline
  if (-not $NoDoor2) { Check-Egress; Check-Door2 }
}
Check-Psoc
Check-Drive
$bad = @($script:results | Where-Object { $_ -eq 'NG' }).Count
$warn = @($script:results | Where-Object { $_ -eq '注意' }).Count
$good = @($script:results | Where-Object { $_ -eq 'OK' }).Count
Write-Host ''
Write-Host "NG $bad 件 / 注意 $warn 件 / OK $good 件"
if ($bad -gt 0) { exit 1 }
exit 0
