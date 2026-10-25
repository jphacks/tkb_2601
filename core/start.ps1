# Robomori core: start the Docker core, then run the PSoC 6 key and the Google Drive copier (Windows).
# Needs only Docker Desktop and the PowerShell that comes with Windows. No Python on the PC:
# everything that uses Python runs inside the containers.
#
#   powershell -ExecutionPolicy Bypass -File core\start.ps1                  with Unity
#   powershell -ExecutionPolicy Bypass -File core\start.ps1 -Sim             with the Unity stand-in (not together with Unity)
#   powershell -ExecutionPolicy Bypass -File core\start.ps1 -DryRun          door 2 sends nothing to Gemini / Bluesky
#   powershell -ExecutionPolicy Bypass -File core\start.ps1 -Keyboard -NoPsoc   no PSoC 6: Enter opens door 2
#   powershell -ExecutionPolicy Bypass -File core\start.ps1 -NoListen        screen on 127.0.0.1 only (not on this PC's IPv4)
#   powershell -ExecutionPolicy Bypass -File core\start.ps1 -NoBrowser       do not open the browser (type the password yourself)
#
# The screen asks for a one-time password. It is shown here at start, and the browser opens the screen and
# signs in with it by itself (the password goes after '#' in the address, which is never sent to the server).
# To open the screen again later: double-click core\open-screen.cmd (a new password, signed in by itself).
# If port 8000 on this PC's IPv4 is taken, the screen uses the first free port of 8080-8089 (or -ListenPort).
# It is served on this PC's IPv4 address by a host-only relay (core\host\HostOnlyRelay.cs): connections from
# other PCs are cut off at once. Each start also makes a new key that only the PSoC 6 key and check_pc.ps1 use.
#
# Ctrl+C stops only the PSoC 6 key (and the relay). The core keeps running and never touches the Unity main line.
# To stop the core too: cd core; docker compose --profile sim down
param(
  [switch]$Sim,
  [switch]$DryRun,
  [switch]$Keyboard,
  [switch]$NoPsoc,
  [string]$Port = '',
  [string]$DriveFolder = 'auto',
  [switch]$NoListen,
  [int]$ListenPort = 8000,
  [switch]$NoBrowser
)

Set-Location -LiteralPath $PSScriptRoot
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
  Write-Host 'docker not found. Start Docker Desktop and try again.'
  exit 1
}

function Find-HostIPv4 {
  # The IPv4 address of the adapter that has a default gateway (the PC's address on its network).
  # .NET first: it answers at once. Get-NetIPConfiguration (CIM) can take seconds, so it is only the fallback.
  foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    if ($nic.OperationalStatus -ne 'Up' -or $nic.NetworkInterfaceType -eq 'Loopback') { continue }
    $props = $nic.GetIPProperties()
    $gateway = @($props.GatewayAddresses | Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' -and $_.Address.ToString() -ne '0.0.0.0' })
    if (-not $gateway.Count) { continue }
    foreach ($address in $props.UnicastAddresses) {
      if ($address.Address.AddressFamily -eq 'InterNetwork') { return $address.Address.ToString() }
    }
  }
  try {
    $config = Get-NetIPConfiguration -ErrorAction Stop | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } | Select-Object -First 1
    if ($config -and $config.IPv4Address) { return [string](@($config.IPv4Address)[0].IPAddress) }
  } catch { }
  return ''
}

function Test-PortFree([string]$Address, [int]$Number) {
  # Same way the host-only relay listens (exclusive), so a free answer here means the relay can start.
  $listener = $null
  try {
    $listener = New-Object -TypeName System.Net.Sockets.TcpListener -ArgumentList @([System.Net.IPAddress]::Parse($Address), $Number)
    $listener.ExclusiveAddressUse = $true
    $listener.Start()
    return $true
  } catch {
    return $false
  } finally {
    if ($listener) { try { $listener.Stop() } catch { } }
  }
}

function Show-PortUser([int]$Number) {
  # Who already listens on the port. Something on every address (0.0.0.0 / ::) could be reached from other PCs.
  try {
    foreach ($row in @(Get-NetTCPConnection -LocalPort $Number -State Listen -ErrorAction Stop)) {
      $name = '?'
      try { $name = (Get-Process -Id $row.OwningProcess -ErrorAction Stop).ProcessName } catch { }
      Write-Host ('  Port {0} is already used by {1} (pid {2}) on {3}' -f $Number, $name, $row.OwningProcess, $row.LocalAddress)
      if ($row.LocalAddress -eq '0.0.0.0' -or $row.LocalAddress -eq '::') {
        Write-Host ('  Warning: {0} listens on every address of this PC, so other PCs may reach port {1}. Run core\host\check_pc.ps1' -f $name, $Number) -ForegroundColor Red
      }
    }
  } catch { }
}

# A new key for this start: the core gives its door state only to the PC side that knows it.
$bytes = New-Object byte[] 32
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$random.GetBytes($bytes)
$random.Dispose()
$hostKey = -join ($bytes | ForEach-Object { $_.ToString('x2') })
$ip = if ($NoListen) { '' } else { Find-HostIPv4 }
if ($ip -and -not (Test-PortFree $ip $ListenPort)) {
  Show-PortUser $ListenPort
  if ($PSBoundParameters.ContainsKey('ListenPort')) {
    Write-Host ('  {0}:{1} is in use. Try another port, for example: -ListenPort 8080' -f $ip, $ListenPort)
  } else {
    foreach ($candidate in 8080..8089) {
      if (Test-PortFree $ip $candidate) {
        Write-Host ('  The screen uses port {0} instead of {1}.' -f $candidate, $ListenPort)
        $ListenPort = $candidate
        break
      }
    }
  }
}
$env:ROBOMORI_HOST_KEY = $hostKey
$env:ROBOMORI_HOST_IP = $ip
$env:ROBOMORI_HOST_PORT = [string]$ListenPort

$composeArgs = @('compose')
if ($Sim) { $composeArgs += @('--profile', 'sim') }
# The virtual VPN's company PDS (core\host\bluesky_setup.ps1 -Vpn) starts with the core
$envFile = Join-Path $PSScriptRoot '.env'
if ((Test-Path -LiteralPath $envFile) -and (Select-String -LiteralPath $envFile -Pattern '^BLUESKY_SERVICE=http://vpn-pds' -Quiet)) { $composeArgs += @('--profile', 'vpn') }
& docker @composeArgs up -d
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Wait up to 60 s for the core to answer. Unity's main line runs either way.
$ready = $false
for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
  try {
    $request = [System.Net.HttpWebRequest]::Create('http://127.0.0.1:8000/healthz')
    $request.Timeout = 2000
    $request.Proxy = $null
    $request.GetResponse().Close()
    $ready = $true
  } catch {
    Start-Sleep -Seconds 1
  }
}
if (-not $ready) {
  Write-Host 'The core does not answer. See: cd core; docker compose logs web hub'
  exit 1
}

$logs = Join-Path (Split-Path -Parent $PSScriptRoot) 'Logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
[System.IO.File]::WriteAllText((Join-Path $logs '.robomori-host-key'), $hostKey)

$pass = ([string](& docker compose exec -T web python manage.py issue_otp --plain 2>$null | Select-Object -Last 1)).Trim()
$screen = if ($ip) { 'http://{0}:{1}' -f $ip, $ListenPort } else { 'http://127.0.0.1:8000' }
Write-Host ''
Write-Host ('  Screen:            {0}   (this PC only)' -f $screen)
$signIn = ''
if ("$pass" -cmatch '^[A-Z2-9]{4}-[A-Z2-9]{4}$') {
  Write-Host  '  One-time password: ' -NoNewline
  Write-Host ("$pass") -ForegroundColor Yellow
  Write-Host  '  (single use, 10 minutes; a new one: double-click core\open-screen.cmd)'
  if (-not $NoBrowser) {
    $signIn = '/login#code=' + $pass
    Write-Host  '  The browser opens the screen and signs in with it by itself.'
  }
} else {
  Write-Host '  No one-time password yet. Make one: powershell -ExecutionPolicy Bypass -File core\host\otp.ps1'
}
Write-Host ''

$key = @{ DriveFolder = $DriveFolder; HostKey = $hostKey; ListenPort = $ListenPort }
if ($ip) { $key.Listen = $ip }
if ($DryRun) { $key.DryRun = $true }
if ($Keyboard) { $key.Keyboard = $true }
if ($NoPsoc) { $key.NoPsoc = $true }
if ($Port) { $key.Port = $Port }
if ($signIn) { $key.OpenScreen = $signIn }
& (Join-Path $PSScriptRoot 'host\psoc6_key.ps1') @key
exit $LASTEXITCODE
