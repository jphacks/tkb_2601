<#
PSoC 6 を扉 2 の鍵にする（Windows。Python は要らない。Windows に最初から入っている PowerShell だけで動く）。
USB の機器は Docker Desktop のコンテナに渡せないので、ここだけ PC 側で動かす。ほかはすべてコンテナの中。

- SW2 が押されたら `docker compose run --rm door2` を 1 回だけ動かす。終わったら扉 2 の出口（egress）もすぐ消す。
- ランプ: 点灯 = 扉 2 が開いている / 速い点滅 = 開けられなかった・失敗 / ゆっくり点滅 = 外へ出す候補あり / 消灯 = なし
  （状態は中核の画面と同じ http://127.0.0.1:8000/api/state から読む）
- 時間割で扉 2 に鍵がかかっている間（単独・店内）は、SW2 を押しても開けない（速い点滅）。
- -DriveFolder auto なら Google Drive for desktop の「マイドライブ\robomori」を探し、書き出された要約を写す。
  写すのは決まった名前の普通のファイルだけ（リンクは辿らない、5 MB まで、書きかけは出さない）。
- M5StickS3（VID 303A）は開かない。開くのは KitProg3（VID 04B4）の COM ポートだけ。
- 中核の状態は /api/key から、起動ごとの鍵（-HostKey。start.ps1 が作る）を付けて読む。画面の合言葉とは別。
- -Listen にこの PC の IPv4 を渡すと、その住所で画面を出す（HostOnlyRelay.cs）。この PC からの接続だけを通す。
- -OpenScreen '/login#code=XXXX-XXXX' なら、画面を出せたあとでブラウザを開き、そのワンタイムパスワードで自動で入る。
  開いた画面の住所は Logs\.robomori-screen に書く（core\host\otp.ps1 が開き直すときに使う）。
- Drive へ写すたびに、書き出しフォルダの .drive-log.jsonl に結果を 1 行書く（画面の「Google Drive の記録」が読む）。

使い方（ふつうは core\start.ps1 から呼ばれる）:
  powershell -ExecutionPolicy Bypass -File core\host\psoc6_key.ps1 -DriveFolder auto
  ... -Keyboard -NoPsoc     PSoC 6 がないとき（Enter で扉 2 を開ける）
  ... -DryRun               扉 2 を開けても Gemini・Bluesky には送らない
  ... -DriveFolder auto -DriveCheck   Drive に届くか試して終わる
#>
param(
  [string]$Port = '',
  [switch]$Keyboard,
  [switch]$NoPsoc,
  [switch]$DryRun,
  [string]$DriveFolder = '',
  [switch]$DriveCheck,
  [string]$ExportFolder = '',
  [string]$StateUrl = 'http://127.0.0.1:8000/api/key',
  [string]$HostKey = '',
  [string]$Listen = '',
  [int]$ListenPort = 8000,
  [string]$OpenScreen = ''
)

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Compose = Join-Path $Root 'core\docker-compose.yml'
if (-not $ExportFolder) { $ExportFolder = Join-Path $Root 'Logs\drive_out' }
$ExportName = '^guardian-records-\d{8}-\d{6}\.(md|jsonl)$'
$KeyFile = Join-Path $Root 'Logs\.robomori-host-key'
if (-not $HostKey -and (Test-Path -LiteralPath $KeyFile)) { $HostKey = ([System.IO.File]::ReadAllText($KeyFile)).Trim() }
$script:StateProblem = ''
$script:CopyProblems = @{}
$ExportLimit = 5MB
$Refused = 3
$FlashSeconds = 2.0
$OutputEncoding = New-Object System.Text.UTF8Encoding $false
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Say([string]$Message) { Write-Host ('{0:HH:mm:ss} {1}' -f (Get-Date), $Message) }

function Get-LampOn($State, [bool]$Running, [double]$FlashUntil, [double]$Now) {
  if ($Running -or ($State -and $State.door2.state -eq 'external')) { return $true }
  if ($Now -lt $FlashUntil) { return ([math]::Floor($Now / 0.25) % 2) -eq 0 }
  if ($State -and $State.door2.pending) { return ([math]::Floor($Now / 0.5) % 2) -eq 0 }
  return $false
}

function Read-State {
  try {
    $request = [System.Net.HttpWebRequest]::Create($StateUrl)
    $request.Timeout = 1000
    $request.Proxy = $null
    if ($HostKey) { $request.Headers.Add('X-Robomori-Key', $HostKey) }
    $response = $request.GetResponse()
    try {
      $reader = New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)
      $script:StateProblem = ''
      return ($reader.ReadToEnd() | ConvertFrom-Json)
    } finally { $response.Close() }
  } catch {
    # PowerShell が .NET の例外を包むので、中の WebException までたどって HTTP の答えを見る
    $e = $_.Exception
    while ($e -and -not ($e -is [System.Net.WebException])) { $e = $e.InnerException }
    $code = if ($e -and $e.Response) { [int]$e.Response.StatusCode } else { 0 }
    $script:StateProblem = if ($code -eq 403) { 'key' } else { 'down' }
    return $null
  }
}

function Write-DriveLog([string]$Folder, [hashtable]$Entry) {
  # 画面の「Google Drive の記録」が読む 1 行（書けなくても写す処理は止めない）
  if (-not $Folder -or -not (Test-Path -LiteralPath $Folder -PathType Container)) { return }
  $Entry.at = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
  try {
    [System.IO.File]::AppendAllText((Join-Path $Folder '.drive-log.jsonl'), ($Entry | ConvertTo-Json -Compress) + "`n", (New-Object System.Text.UTF8Encoding $false))
  } catch { }
}

function Find-PsocPort {
  # KitProg3（PSoC 6）の COM ポートだけを探す。StickS3（VID 303A）は対象にしない
  try {
    $devices = Get-CimInstance -ClassName Win32_PnPEntity -Filter "PNPDeviceID LIKE 'USB\\VID_04B4%'" -ErrorAction Stop
  } catch { return $null }
  foreach ($device in $devices) { if ($device.Name -match '\((COM\d+)\)') { return $Matches[1] } }
  return $null
}

function Find-DriveFolder {
  $roots = @()
  foreach ($letter in 'GHIJKLMNOPQRSTUVWXYZDEF'.ToCharArray()) {
    foreach ($name in @('マイドライブ', 'My Drive')) { $roots += ('{0}:\{1}' -f $letter, $name) }
  }
  foreach ($name in @('マイドライブ', 'My Drive')) { $roots += (Join-Path $HOME $name) }   # ミラーリングのとき
  foreach ($root in $roots) {
    if (Test-Path -LiteralPath $root -PathType Container) {
      $folder = Join-Path $root 'robomori'
      if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        try { New-Item -ItemType Directory -Path $folder -ErrorAction Stop | Out-Null } catch { continue }
      }
      return $folder
    }
  }
  return $null
}

function Resolve-DriveFolder([string]$Value) {
  if (-not $Value -or $Value -eq 'off') { return $null }
  if ($Value -eq 'auto') {
    $found = Find-DriveFolder
    if (-not $found) { Say 'Google Drive for desktop が見つからない。入れて店のアカウントでログインすると、要約を Drive に写せる（いまは写さない）' }
    return $found
  }
  if (-not (Test-Path -LiteralPath $Value -PathType Container)) {
    $parent = Split-Path -Parent $Value
    if ($parent -and (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $Value -ErrorAction SilentlyContinue | Out-Null }
  }
  if (Test-Path -LiteralPath $Value -PathType Container) { return $Value }
  Say "Drive の同期フォルダが見つからない: $Value"
  return $null
}

function Copy-NewExports([string]$Source, [string]$Target, $Done) {
  $copied = @()
  if (-not (Test-Path -LiteralPath $Source -PathType Container)) { return $copied }
  foreach ($file in (Get-ChildItem -LiteralPath $Source -File -Force -ErrorAction SilentlyContinue | Sort-Object Name)) {
    $name = $file.Name
    if ($Done.Contains($name) -or $name -notmatch $ExportName) { continue }
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or $file.Length -gt $ExportLimit) { [void]$Done.Add($name); continue }
    $destination = Join-Path $Target $name
    if ((Test-Path -LiteralPath $destination) -and ((Get-Item -LiteralPath $destination).Length -eq $file.Length)) { [void]$Done.Add($name); continue }
    $partial = Join-Path $Target ('.' + $name + '.part')
    try {
      Copy-Item -LiteralPath $file.FullName -Destination $partial -Force -ErrorAction Stop
      Move-Item -LiteralPath $partial -Destination $destination -Force -ErrorAction Stop
      [void]$Done.Add($name)
      $copied += $name
      $script:CopyProblems.Remove($name)
      Write-DriveLog $Source @{ kind = 'copy'; file = $name; ok = $true; to = $Target }
    } catch {
      # 同期ソフトが掴んでいるときなど。次の回に試し直す（同じ理由は 1 回だけ記録する）
      $problem = $_.Exception.Message
      if ($script:CopyProblems[$name] -ne $problem) {
        $script:CopyProblems[$name] = $problem
        Write-DriveLog $Source @{ kind = 'copy'; file = $name; ok = $false; to = $Target; error = $problem }
      }
    }
  }
  return $copied
}

function Stop-Egress {
  # 扉 2 の出口の中継を、すぐ消す（中継は 30 秒使われなければ自分でも終わる）
  $null = & docker compose -f $Compose --profile door2 rm --stop --force egress 2>&1
}

function Start-Door2([string]$Source) {
  $out = [System.IO.Path]::GetTempFileName()
  $err = [System.IO.Path]::GetTempFileName()
  $arguments = @('compose', '-f', ('"' + $Compose + '"'), 'run', '--rm', '-e', ('"OPENED_BY=' + $Source + '"'))
  if ($DryRun) { $arguments += @('-e', 'DOOR2_DRY_RUN=1') }
  $arguments += 'door2'
  $process = Start-Process -FilePath 'docker' -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
  $null = $process.Handle   # 終わったあとに ExitCode を読めるようにする
  return @{ Process = $process; Out = $out; Err = $err }
}

function Finish-Door2($Run) {
  foreach ($path in @($Run.Out, $Run.Err)) {
    foreach ($line in (Get-Content -LiteralPath $path -Encoding UTF8 -ErrorAction SilentlyContinue)) {
      if ($line.Trim() -and $line -notmatch 'Container|Network') { Say ('  ' + $line.Trim()) }
    }
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
  }
  Stop-Egress
  return $Run.Process.ExitCode
}

# ---- はじめ ----

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
  Say 'docker が見つからない。Docker Desktop を起動してから、もう一度'
  exit 1
}
$drive = Resolve-DriveFolder $DriveFolder
$done = New-Object 'System.Collections.Generic.HashSet[string]'
if ($DriveFolder -and $DriveFolder -ne 'off') {
  if ($drive) { Write-DriveLog $ExportFolder @{ kind = 'drive'; ok = $true; to = $drive } }
  else { Write-DriveLog $ExportFolder @{ kind = 'drive'; ok = $false; to = ''; error = 'Google Drive for desktop の同期フォルダが見つからない' } }
}

if ($DriveCheck) {
  if (-not $drive) { Say 'Drive の同期フォルダが決まらない（-DriveFolder auto か、フォルダを指定する）'; exit 1 }
  $check = Join-Path $drive ('robomori-drive-check-{0:yyyyMMdd-HHmmss}.txt' -f (Get-Date))
  $partial = Join-Path $drive ('.' + (Split-Path -Leaf $check) + '.part')
  [System.IO.File]::WriteAllText($partial, ('ロボもり: Google Drive へ写せるかの確認（{0:yyyy-MM-dd HH:mm:ss}）' -f (Get-Date)) + "`n", (New-Object System.Text.UTF8Encoding $false))
  Move-Item -LiteralPath $partial -Destination $check -Force
  $copied = Copy-NewExports $ExportFolder $drive $done
  Say "確認用のファイルを置いた: $check"
  Say ("たまっていた要約を {0} 件写した（{1} → {2}）" -f $copied.Count, $ExportFolder, $drive)
  Say ('Google Drive（drive.google.com）の「robomori」フォルダに {0} が出れば、つながっている' -f (Split-Path -Leaf $check))
  exit 0
}

$serial = $null
$buffer = ''
$door = @{ Running = $false; FlashUntil = 0.0; Run = $null }
$state = $null
$reachable = $null
$missingNoted = $false
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$nextLamp = 0.0; $nextState = 0.0; $nextCopy = 0.0; $nextPort = 0.0
$openedAt = 0.0; $heard = $false; $oddNoted = $false; $silentNoted = $false; $lastOpenProblem = ''

function Open-Door2([string]$Source) {
  if ($door.Running -or ($state -and $state.door2.state -eq 'external')) { Say '扉 2 はもう開いている'; return }
  if ($state -and $state.schedule -and $state.schedule.door2_allowed -eq $false) {
    $door.FlashUntil = $clock.Elapsed.TotalSeconds + $FlashSeconds
    Say ('時間割が「{0}」のあいだは、扉 2 を開けない' -f $state.schedule.label)
    return
  }
  $door.Running = $true
  Say "扉 2 を開ける（$Source）。door2 コンテナを立ち上げる"
  $door.Run = Start-Door2 $Source
}

Say "PSoC 6 を扉 2 の鍵として待つ（中核: $StateUrl）。終わるときは Ctrl+C"
if ($drive) { Say "書き出された要約を Google Drive へ写す: $ExportFolder → $drive" }
if (-not $HostKey) { Say '起動ごとの鍵がない。core\start.ps1 から起動すると、中核の状態を読める' }
$relay = $null
if ($Listen) {
  try {
    if (-not ('Robomori.HostOnlyRelay' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'HostOnlyRelay.cs') }
    $relay = [Robomori.HostOnlyRelay]::Start($Listen, $ListenPort, 8000)
    Say ('画面: http://{0}:{1}（この PC からだけ開ける。ほかの PC からの接続はすぐ切る）' -f $Listen, $ListenPort)
  } catch {
    Say ('{0}:{1} で画面を出せなかった（{2}）。画面は http://127.0.0.1:8000 で開く' -f $Listen, $ListenPort, $_.Exception.Message)
  }
}
# 画面を出せた住所を覚え、頼まれていればブラウザで開く（合言葉は # の後ろなので、サーバーには送られない）
$screenBase = if ($relay) { 'http://{0}:{1}' -f $Listen, $ListenPort } else { 'http://127.0.0.1:8000' }
try { [System.IO.File]::WriteAllText((Join-Path $Root 'Logs\.robomori-screen'), $screenBase) } catch { }
if ($OpenScreen -cmatch '^/login#code=[A-Z2-9]{4}-[A-Z2-9]{4}$') {
  try {
    Start-Process ($screenBase + $OpenScreen)
    Say ('ブラウザで画面を開いた（{0}）。ワンタイムパスワードで自動で入る' -f $screenBase)
  } catch {
    Say ('ブラウザを開けなかった。{0} を開いて、上のワンタイムパスワードを入れる' -f $screenBase)
  }
}
$refusedSeen = 0
try {
  while ($true) {
    $now = $clock.Elapsed.TotalSeconds

    # PSoC 6 をつなぐ（見つかるまで 1 秒ごと）
    if (-not $NoPsoc -and $null -eq $serial -and $now -ge $nextPort) {
      $nextPort = $now + 1.0
      $name = if ($Port) { $Port } else { Find-PsocPort }
      if (-not $name) {
        if (-not $missingNoted) { Say 'PSoC 6 が見つからない。KitProg3 側の USB をつなぐと自動で接続する'; $missingNoted = $true }
      } else {
        try {
          $serial = New-Object System.IO.Ports.SerialPort $name, 115200
          $serial.NewLine = "`n"; $serial.ReadTimeout = 50; $serial.WriteTimeout = 200
          $serial.DtrEnable = $true; $serial.RtsEnable = $true
          $serial.Open()
          $buffer = ''; $missingNoted = $false
          $openedAt = $now; $heard = $false; $oddNoted = $false; $silentNoted = $false; $lastOpenProblem = ''
          Say "PSoC 6 に接続: $name。PSoC 6 のプログラム（main.py）の返事を待つ"
        } catch {
          $problem = $_.Exception.Message
          if ($problem -ne $lastOpenProblem) {
            $lastOpenProblem = $problem
            $hint = if ($problem -match 'denied|拒否') { '。ほかのプログラム（前に動かした鍵、psoc6_setup.ps1、Tera Term など）が使っている。止めると自動でつなぐ' } else { '' }
            Say "PSoC 6（$name）を開けなかった: $problem$hint"
          }
          if ($serial) { $serial.Dispose() }
          $serial = $null
        }
      }
    }

    # PSoC 6 からの行（SW2 が押された、など）
    if ($null -ne $serial) {
      try {
        if ($serial.BytesToRead -gt 0) { $buffer += $serial.ReadExisting() }
        while ($buffer.Contains("`n")) {
          $index = $buffer.IndexOf("`n")
          $line = $buffer.Substring(0, $index).Trim()
          $buffer = $buffer.Substring($index + 1)
          if ($line.StartsWith('{')) {
            try { $message = $line | ConvertFrom-Json } catch { $message = $null }
            if ($message -and $message.type -eq 'event' -and $message.event -eq 'hello' -and -not $heard) {
              $heard = $true
              Say ('PSoC 6 のプログラムが答えた（{0}、{1}）。SW2（USER ボタン）を押すと扉 2 が開く' -f $message.dev, $message.fw)
            }
            if ($message -and $message.type -eq 'event' -and $message.event -eq 'button') { Open-Door2 '人が PSoC 6 の SW2 を押した' }
          } elseif ($line -and -not $oddNoted) {
            $oddNoted = $true
            Say "PSoC 6 から、決まった形ではない行が届いた: $line（main.py が動いていないかもしれない）"
          }
        }
      } catch {
        Say "PSoC 6 との接続が切れた: $($_.Exception.Message)"
        try { $serial.Dispose() } catch { }
        $serial = $null
      }
    }

    # つないで 6 秒たっても PSoC 6 のプログラムが答えないとき（main.py は起動時と約 2 秒ごとに hello を返す）
    if ($null -ne $serial -and -not $heard -and -not $silentNoted -and ($now - $openedAt) -gt 6) {
      $silentNoted = $true
      Say 'PSoC 6 のプログラムが答えない。main.py（MicroPython）が書き込まれていないか、止まっている（firmware\psoc6_share_panel\README.md）。RESET を押すと起動し直す'
    }

    # Enter キー（-Keyboard のとき）
    if ($Keyboard) {
      try {
        while ([Console]::KeyAvailable) {
          if ([Console]::ReadKey($true).Key -eq [ConsoleKey]::Enter) { Open-Door2 '人が Enter キーを押した' }
        }
      } catch { }
    }

    # door2 が終わったか
    if ($door.Running -and $door.Run -and $door.Run.Process.HasExited) {
      $code = Finish-Door2 $door.Run
      $door.Running = $false; $door.Run = $null
      if ($code -eq 0) { Say '扉 2 を閉じた（door2 コンテナと出口の中継は消えた）' }
      elseif ($code -eq $Refused) { $door.FlashUntil = $now + $FlashSeconds; Say '時間割で鍵がかかっていたので、扉 2 は開けなかった' }
      else { $door.FlashUntil = $now + $FlashSeconds; Say "扉 2 のやりとりで、うまくいかないことがあった（終了コード $code）。扉 2 は閉じた" }
    }

    # 中核の状態
    if ($now -ge $nextState) {
      $nextState = $now + 0.5
      $state = Read-State
      if (($null -ne $state) -ne $reachable) {
        $reachable = ($null -ne $state)
        if ($reachable) { Say '中核に届いた' }
        elseif ($script:StateProblem -eq 'key') { Say '中核に届いたが、起動ごとの鍵が合わない（core\start.ps1 で起動し直す）' }
        else { Say '中核に届かない（core\start.ps1 で起動する）' }
      }
      if ($relay -and $relay.Refused -ne $refusedSeen) {
        $refusedSeen = $relay.Refused
        Say ('ほかの PC（{0}）から画面につなごうとしたので、切った（累計 {1} 回）' -f $relay.LastRefused, $refusedSeen)
      }
    }

    # Google Drive へ写す
    if ($drive -and $now -ge $nextCopy) {
      $nextCopy = $now + 2.0
      foreach ($name in (Copy-NewExports $ExportFolder $drive $done)) { Say "Google Drive の同期フォルダへ写した: $name" }
    }

    # ランプ（PSoC 6 はこの行を受けてから SW2 の知らせを返すので、つながっている間は送り続ける）
    if ($null -ne $serial -and $now -ge $nextLamp) {
      $nextLamp = $now + 0.2
      $on = Get-LampOn $state $door.Running $door.FlashUntil $now
      try { $serial.WriteLine('{"cmd":"lamp","on":' + $(if ($on) { 'true' } else { 'false' }) + '}') } catch { }
    }

    Start-Sleep -Milliseconds 50
  }
} finally {
  if ($relay) { $relay.Stop() }
  if ($null -ne $serial) {
    try { $serial.WriteLine('{"cmd":"lamp","on":false}') } catch { }
    try { $serial.Dispose() } catch { }
  }
  Say '終了'
}
