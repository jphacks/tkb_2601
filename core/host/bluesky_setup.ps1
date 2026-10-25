<#
Bluesky（bsky.social）か、仮想 VPN の社内 PDS を使うように core\.env を組み、つながるかを確かめる（Windows）。
アプリパスワードは画面に出さずに受け取り、core\.env（Git に入れない）にだけ書く。投稿はしない。

  powershell -ExecutionPolicy Bypass -File core\host\bluesky_setup.ps1            bsky.social の店のアカウントを使う
  powershell -ExecutionPolicy Bypass -File core\host\bluesky_setup.ps1 -Vpn       仮想 VPN の社内 PDS（A 社）を使う
  ... -Vpn -Company b      B 社のアカウントにする
  ... -Write               確かめたあと、確認用の暗号化した記録を 1 件書く（画面で宛先を入れてから）
  ... -NoCheck             書くだけで、確かめない
#>
param(
  [switch]$Vpn,
  [ValidateSet('a', 'b')][string]$Company = 'a',
  [switch]$Write,
  [switch]$NoCheck
)

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Core = Join-Path $Root 'core'
$EnvFile = Join-Path $Core '.env'
$Compose = Join-Path $Core 'docker-compose.yml'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

if (-not (Test-Path -LiteralPath $EnvFile)) { Copy-Item -LiteralPath (Join-Path $Core '.env.example') -Destination $EnvFile }
$lines = [System.Collections.Generic.List[string]]::new()
foreach ($line in [System.IO.File]::ReadAllLines($EnvFile)) { $lines.Add($line) }

function Set-Value([string]$Key, [string]$Value) {
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match ('^\s*#?\s*' + [regex]::Escape($Key) + '=')) { $lines[$i] = "$Key=$Value"; return }
  }
  $lines.Add("$Key=$Value")
}

function Get-Value([string]$Key) {
  foreach ($line in $lines) { if ($line -match ('^' + [regex]::Escape($Key) + '=(.*)$')) { return $Matches[1].Trim() } }
  return ''
}

function New-Secret {
  # 推測されない合言葉（24 バイトの乱数）。.env にだけ書き、画面には出さない
  $bytes = New-Object byte[] 24
  $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
  return ([Convert]::ToBase64String($bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

if ($Vpn) {
  # 仮想 VPN: 書き込み先も返信を読む先も社内 PDS。出口の中継は、外のどこにも通さない。
  # 会社のアカウントのパスワードは、初めての時にだけランダムに作って .env に書く（コードに既定のパスワードはない）
  $accounts = Get-Value 'VPN_PDS_ACCOUNTS'
  if ($accounts -notmatch 'shop-a\.vpn\.internal:' -or $accounts -notmatch 'shop-b\.vpn\.internal:') {
    $accounts = "shop-a.vpn.internal:$(New-Secret),shop-b.vpn.internal:$(New-Secret)"
    Set-Value 'VPN_PDS_ACCOUNTS' $accounts
    Write-Host '仮想 VPN の会社のアカウント（A 社・B 社）を、ランダムなパスワードで作った（.env にだけ書いた）'
  }
  $handle = "shop-$Company.vpn.internal"
  $password = (($accounts -split ',') | Where-Object { $_ -like "${handle}:*" } | Select-Object -First 1).Substring($handle.Length + 1)
  Set-Value 'BLUESKY_HANDLE' $handle
  Set-Value 'BLUESKY_APP_PASSWORD' $password
  Set-Value 'BLUESKY_SERVICE' 'http://vpn-pds:2583'
  Set-Value 'BLUESKY_APPVIEW' 'http://vpn-pds:2583'
  Set-Value 'EGRESS_ALLOW' 'nothing-outside.invalid'
  Set-Value 'EGRESS_INTERNAL' ''
  Write-Host "仮想 VPN の社内 PDS を使う: @$handle（外へは何も出さない）"
} else {
  $handle = (Read-Host '店のアカウントのハンドル（例: robomori-demo.bsky.social）').Trim().TrimStart('@')
  if ($handle -notmatch '^[A-Za-z0-9.-]+\.[A-Za-z]{2,}$') { Write-Host 'ハンドルの形ではない' -ForegroundColor Red; exit 1 }
  $secure = Read-Host 'アプリパスワード（bsky.app の 設定 → プライバシーとセキュリティ → アプリパスワード。画面には出ない）' -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try { $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
  if ($password -notmatch '^[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}$') {
    Write-Host 'アプリパスワードの形（xxxx-xxxx-xxxx-xxxx）ではない。ログイン用の本パスワードは使わない' -ForegroundColor Red; exit 1
  }
  Set-Value 'BLUESKY_HANDLE' $handle
  Set-Value 'BLUESKY_APP_PASSWORD' $password
  Set-Value 'BLUESKY_SERVICE' ''
  Set-Value 'BLUESKY_APPVIEW' ''
  Set-Value 'EGRESS_ALLOW' ''
  Set-Value 'EGRESS_INTERNAL' ''
  Write-Host "bsky.social の @$handle を使う"
}
[System.IO.File]::WriteAllText($EnvFile, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding $false))
Write-Host "書いた: $EnvFile"

# .env が Git に入らないことを確かめる
Push-Location $Root
try {
  & git check-ignore -q -- 'core/.env' 2>$null
  if ($LASTEXITCODE -ne 0) { Write-Host '注意: core\.env が .gitignore に入っていない。GitHub に上げないこと' -ForegroundColor Yellow }
  else { Write-Host 'core\.env は Git に入らない（.gitignore）' }
} finally { Pop-Location }

if ($NoCheck) { exit 0 }
if ($Vpn) {
  & docker compose -f $Compose --profile vpn up -d vpn-pds
  if ($LASTEXITCODE -ne 0) { Write-Host '仮想 VPN の社内 PDS を起動できなかった。docker compose build を先に' -ForegroundColor Red; exit 1 }
  Start-Sleep -Seconds 2
}
$check = @('compose', '-f', $Compose, 'run', '--rm', 'door2', 'python', 'manage.py', 'bsky_check')
if ($Write) { $check += '--write' }
& docker @check
exit $LASTEXITCODE
