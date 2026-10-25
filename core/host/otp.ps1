<#
画面に入るワンタイムパスワードを新しく出し、ブラウザで画面を開いて自動で入る（Windows。Python は要らない）。
前に出したものは使えなくなる。中核（core\start.ps1）が動いているときに使う。
いちばん簡単なのは core\open-screen.cmd のダブルクリック（これを呼ぶだけ）。
合言葉はこの画面にだけ出る（中核の DB にはハッシュだけ）。ブラウザには「/login#code=…」で渡し、# の後ろはサーバーに送られない。

  powershell -ExecutionPolicy Bypass -File core\host\otp.ps1               新しく出して、ブラウザで開く（10 分・1 回だけ）
  ... -NoBrowser     ブラウザは開かない（出すだけ。手で入れる）
  ... -LogoutAll     いま入っている画面も、すべて入り直しにする
  ... -Minutes 30    使える時間を変える（1〜60 分）
#>
param(
  [switch]$LogoutAll,
  [int]$Minutes = 0,
  [switch]$NoBrowser
)

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Compose = Join-Path $Root 'core\docker-compose.yml'
$ScreenFile = Join-Path $Root 'Logs\.robomori-screen'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$arguments = @('compose', '-f', $Compose, 'exec', '-T', 'web', 'python', 'manage.py', 'issue_otp', '--plain')
if ($LogoutAll) { $arguments += '--logout-all' }
if ($Minutes -gt 0) { $arguments += @('--minutes', [string]$Minutes) }
$output = @(& docker @arguments 2>&1 | ForEach-Object { "$_" })
$code = [string]($output | ForEach-Object { $_.Trim() } | Where-Object { $_ -cmatch '^[A-Z2-9]{4}-[A-Z2-9]{4}$' } | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or $code -cnotmatch '^[A-Z2-9]{4}-[A-Z2-9]{4}$') {
  Write-Host '中核が動いていない。core\start.ps1 で起動してから' -ForegroundColor Red
  Write-Host ($output -join "`n")
  exit 1
}

# 画面の住所は、start.ps1（の PSoC 6 の鍵）が画面を出せた住所。なければ 127.0.0.1
$screen = 'http://127.0.0.1:8000'
if (Test-Path -LiteralPath $ScreenFile) {
  $saved = ([System.IO.File]::ReadAllText($ScreenFile)).Trim()
  if ($saved -match '^http://[0-9.]+:[0-9]{2,5}$') { $screen = $saved }
}
Write-Host ''
Write-Host ('  画面:                 {0}' -f $screen)
Write-Host  '  ワンタイムパスワード: ' -NoNewline
Write-Host $code -ForegroundColor Yellow
if ($LogoutAll) { Write-Host '  入っていた画面は、すべて入り直しにした' }
if ($NoBrowser) {
  Write-Host '  （1 回だけ使える。ブラウザで上の画面を開き、このパスワードを入れる）'
} else {
  try {
    Start-Process ($screen + '/login#code=' + $code)
    Write-Host '  ブラウザで画面を開いた。このパスワードで自動で入る'
  } catch {
    Write-Host '  ブラウザを開けなかった。上の画面を開き、このパスワードを入れる'
  }
}
Write-Host ''
exit 0
