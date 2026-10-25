<#
PSoC 6（CY8CPROTO-062-4343W）に、MicroPython と私たちの main.py を書き込む（Windows。Python は要らない）。
Infineon の書き込みツール（mpy-psoc6.py device-setup）と同じ手順を、PowerShell だけで行う。
  1. Infineon の openocd（書き込み器 KitProg3 を使う公式のツール）と、ボード用の設定を取ってくる（初回だけ。%LOCALAPPDATA%\robomori\psoc6）
  2. Infineon の MicroPython（ボード用の .hex）を取ってきて、PSoC 6 に書き込む
  3. COM ポート越しに MicroPython の REPL で main.py を置き、起動し直して、返事（hello）を確かめる
書き換えるのは PSoC 6 だけ。openocd が話すのは KitProg3（書き込み器）だけで、M5StickS3 には触らない。
使う前に、PSoC 6 の鍵（start.ps1）を Ctrl+C で止める（COM ポートを取り合うため）。

  powershell -ExecutionPolicy Bypass -File core\host\psoc6_setup.ps1                 MicroPython を書き込み、main.py を置く
  ... -SkipFlash       main.py だけ置き直す（MicroPython はもう入っている）
  ... -UpdateKitProg   書き込みに失敗したとき: KitProg3（書き込み器）の中身を新しくしてから書き込む
  ... -Port COM3       COM ポートを決める（省略すると KitProg3 を探す）
#>
param(
  [switch]$SkipFlash,
  [switch]$UpdateKitProg,
  [string]$Port = '',
  [string]$Version = 'latest'
)

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$MainPy = Join-Path $Root 'firmware\psoc6_share_panel\main.py'
$Board = 'CY8CPROTO-062-4343W'
$Tools = Join-Path $env:LOCALAPPDATA 'robomori\psoc6'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell 5.1 は進み具合の表示でダウンロードがとても遅くなる
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Say([string]$Message) { Write-Host ('{0:HH:mm:ss} {1}' -f (Get-Date), $Message) }
function Fail([string]$Message) { Write-Host ('{0:HH:mm:ss} {1}' -f (Get-Date), $Message) -ForegroundColor Red; exit 1 }

function Get-File([string]$Url, [string]$Path, [switch]$Fresh) {
  if ($Fresh -and (Test-Path -LiteralPath $Path)) { Remove-Item -LiteralPath $Path -Force }
  if (Test-Path -LiteralPath $Path) { return }
  Say "取ってくる: $Url"
  $partial = $Path + '.part'
  try { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial -ErrorAction Stop }
  catch { Fail "取ってこられなかった: $Url（$($_.Exception.Message)）。インターネットにつながっているか確かめる" }
  Move-Item -LiteralPath $partial -Destination $Path -Force
}

function Find-PsocPort {
  try { $devices = Get-CimInstance -ClassName Win32_PnPEntity -Filter "PNPDeviceID LIKE 'USB\\VID_04B4%'" -ErrorAction Stop } catch { return $null }
  foreach ($device in $devices) { if ($device.Name -match '\((COM\d+)\)') { return $Matches[1] } }
  return $null
}

function Invoke-OpenOcd([string]$Hex) {
  $openocd = Join-Path $Tools 'openocd\bin\openocd.exe'
  $scripts = Join-Path $Tools 'openocd\scripts'
  $boardDir = Join-Path $Tools 'openocd\board'
  $hexPath = $Hex.Replace('\', '/')
  $command = "source [find interface/kitprog3.cfg]; source [find target/psoc6_2m.cfg]; psoc6 allow_efuse_program off; psoc6 sflash_restrictions 1; program {$hexPath} verify reset exit;"
  $output = (& $openocd -s $scripts -s $boardDir -c $command 2>&1 | ForEach-Object { "$_" }) -join "`n"
  if ($LASTEXITCODE -ne 0 -or $output -notmatch 'Verified OK') {
    Write-Host $output
    Fail ('書き込みに失敗した（{0}）。KitProg3 側の USB でつながっているか確かめ、それでもだめなら -UpdateKitProg を付けてもう一度' -f (Split-Path -Leaf $Hex))
  }
}

# ---- 1. 2. MicroPython を書き込む ----

if (-not (Test-Path -LiteralPath $MainPy)) { Fail "main.py が見つからない: $MainPy" }
if (-not $SkipFlash) {
  New-Item -ItemType Directory -Force -Path $Tools | Out-Null
  if ($UpdateKitProg) {
    $loaderZip = Join-Path $Tools 'fw-loader.zip'
    Get-File 'https://github.com/Infineon/Firmware-loader/releases/download/3.5.0/fw-loader-3.5.0.2114-kitprog3-package-2.50.0.1383-windows.zip' $loaderZip
    if (-not (Test-Path -LiteralPath (Join-Path $Tools 'fw-loader\bin\fw-loader.exe'))) { Expand-Archive -LiteralPath $loaderZip -DestinationPath $Tools -Force }
    Say 'KitProg3（書き込み器）の中身を新しくする'
    & (Join-Path $Tools 'fw-loader\bin\fw-loader.exe') --update-kp3 all
    Say 'KitProg3 が起動し直すのを待つ（10 秒）'
    Start-Sleep -Seconds 10
  }
  $ocdZip = Join-Path $Tools 'openocd.zip'
  Get-File 'https://github.com/Infineon/openocd/releases/download/release-v5.1.0/openocd-5.1.0.3099-windows.zip' $ocdZip
  if (-not (Test-Path -LiteralPath (Join-Path $Tools 'openocd\bin\openocd.exe'))) {
    Say 'openocd を広げる'
    Expand-Archive -LiteralPath $ocdZip -DestinationPath $Tools -Force
  }
  if (-not (Test-Path -LiteralPath (Join-Path $Tools 'openocd\bin\openocd.exe'))) { Fail "openocd.exe が見つからない（$Tools\openocd\bin）" }
  New-Item -ItemType Directory -Force -Path (Join-Path $Tools 'openocd\board') | Out-Null
  Get-File "https://github.com/infineon/micropython/releases/download/v0.3.0/qspi_config_$Board.cfg" (Join-Path $Tools 'openocd\board\qspi_config.cfg')
  $hello = Join-Path $Tools "hello-world_$Board.hex"
  Get-File "https://github.com/infineon/micropython/releases/download/v0.3.0/hello-world_$Board.hex" $hello
  $mpy = Join-Path $Tools "mpy-psoc6_${Version}_$Board.hex"
  $mpyUrl = if ($Version -eq 'latest') { "https://github.com/infineon/micropython/releases/latest/download/mpy-psoc6_$Board.hex" } else { "https://github.com/infineon/micropython/releases/download/$Version/mpy-psoc6_$Board.hex" }
  Get-File $mpyUrl $mpy -Fresh:($Version -eq 'latest')
  Say 'PSoC 6 に書き込む（1/2: 準備）'
  Invoke-OpenOcd $hello
  Say 'PSoC 6 に書き込む（2/2: MicroPython）'
  Invoke-OpenOcd $mpy
  Say 'MicroPython を書き込んだ。起動を待つ（3 秒）'
  Start-Sleep -Seconds 3
}

# ---- 3. main.py を置く（MicroPython の raw REPL） ----

if (-not $Port) { $Port = Find-PsocPort }
if (-not $Port) { Fail 'PSoC 6（KitProg3）の COM ポートが見つからない。KitProg3 側の USB をつなぐ' }
$serial = New-Object System.IO.Ports.SerialPort $Port, 115200
$serial.ReadTimeout = 200; $serial.WriteTimeout = 2000; $serial.DtrEnable = $true; $serial.RtsEnable = $true
try { $serial.Open() } catch { Fail "$Port を開けなかった（$($_.Exception.Message)）。PSoC 6 の鍵（start.ps1）を Ctrl+C で止めてから" }

function Read-Until([string]$Marker, [double]$Seconds) {
  $deadline = (Get-Date).AddSeconds($Seconds)
  $text = ''
  while ((Get-Date) -lt $deadline) {
    if ($serial.BytesToRead -gt 0) {
      $text += $serial.ReadExisting()
      if ($text.Contains($Marker)) { return $text }
    } else { Start-Sleep -Milliseconds 20 }
  }
  return $null
}

function Send-Slowly([string]$Text) {
  # 受け側の入力バッファがあふれないよう、少しずつ送る
  for ($i = 0; $i -lt $Text.Length; $i += 32) {
    $serial.Write($Text.Substring($i, [math]::Min(32, $Text.Length - $i)))
    Start-Sleep -Milliseconds 8
  }
}

function Invoke-Raw([string]$Code) {
  # raw REPL: コードを送って Ctrl-D。返事は "OK" + 出力 + Ctrl-D + エラー + Ctrl-D + ">"
  Send-Slowly $Code
  $serial.Write([string][char]4)
  $reply = Read-Until ([string][char]4 + '>') 15
  if ($null -eq $reply -or -not $reply.Contains('OK')) { Fail "PSoC 6 が答えなかった（送ったもの: $($Code.Substring(0, [math]::Min(40, $Code.Length)))…）" }
  $parts = $reply.Substring($reply.IndexOf('OK') + 2).Split([char]4)
  if ($parts.Length -ge 2 -and $parts[1].Trim()) { Fail "PSoC 6 でエラー: $($parts[1].Trim())" }
  return $parts[0]
}

try {
  Say "$Port で PSoC 6 の MicroPython につなぐ"
  $serial.Write("`r" + [char]3 + [char]3)   # 動いている main.py を止める
  Start-Sleep -Milliseconds 400
  $serial.DiscardInBuffer()
  $serial.Write("`r" + [char]1)             # raw REPL に入る
  if ($null -eq (Read-Until 'raw REPL; CTRL-B to exit' 5)) {
    Fail 'MicroPython が答えない。MicroPython が入っていないなら -SkipFlash を付けずに実行する。入っているなら RESET を押してからもう一度'
  }
  $null = Read-Until '>' 1
  $bytes = [System.IO.File]::ReadAllBytes($MainPy)
  Say ('main.py（{0} バイト）を置く' -f $bytes.Length)
  $null = Invoke-Raw "try:`n import binascii`nexcept ImportError:`n import ubinascii as binascii`nf=open('main.py','wb')"
  for ($i = 0; $i -lt $bytes.Length; $i += 128) {
    $count = [math]::Min(128, $bytes.Length - $i)
    $hex = -join ($bytes[$i..($i + $count - 1)] | ForEach-Object { $_.ToString('x2') })
    $null = Invoke-Raw "f.write(binascii.unhexlify('$hex'))"
  }
  $null = Invoke-Raw 'f.close()'
  $size = (Invoke-Raw "import os`nprint(os.stat('main.py')[6])").Trim()
  if ($size -ne [string]$bytes.Length) { Fail "置いた main.py の大きさが合わない（PSoC 6: $size バイト、PC: $($bytes.Length) バイト）" }
  Say "main.py を置いた（$size バイト、PC 側と同じ）"
  $serial.Write([string][char]2)             # raw REPL を出る
  Start-Sleep -Milliseconds 300
  $serial.DiscardInBuffer()
  $serial.Write([string][char]4)             # 起動し直す（main.py が動き出す）
  $started = Read-Until '"hello"' 10
  if ($null -eq $started) { Fail 'main.py が動き出さなかった。RESET を押して、start.ps1 で「PSoC 6 のプログラムが答えた」と出るか確かめる' }
  Say 'main.py が動き出した（hello が届いた）。start.ps1 で PSoC 6 の鍵を動かせる'
} finally {
  try { $serial.Dispose() } catch { }
}
exit 0
