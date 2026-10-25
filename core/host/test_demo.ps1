# Run the real launcher against an isolated fixture; Docker, hardware and browsers are never started.
$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot 'demo.ps1'
$engine = (Get-Process -Id $PID).Path
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('robomori demo test ' + [Guid]::NewGuid().ToString('N'))
$savedLog = $env:ROBOMORI_DEMO_TEST_LOG
$savedFailure = $env:ROBOMORI_DEMO_TEST_FAILURE

try {
    $hostDir = Join-Path $fixture 'core/host'
    New-Item -ItemType Directory -Path $hostDir -Force | Out-Null
    Copy-Item -LiteralPath $launcher -Destination (Join-Path $hostDir 'demo.ps1')
    Set-Content -LiteralPath (Join-Path $fixture 'core/.env') -Value 'BLUESKY_SERVICE=http://vpn-pds' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $hostDir 'bluesky_setup.ps1') -Encoding ASCII -Value @'
throw 'The fixture must never configure a real account'
'@
    Set-Content -LiteralPath (Join-Path $fixture 'core/start.ps1') -Encoding ASCII -Value @'
param([switch]$Keyboard, [switch]$NoPsoc)
if (-not ($Keyboard -and $NoPsoc)) { throw 'Launcher switches were not forwarded' }
Add-Content -LiteralPath $env:ROBOMORI_DEMO_TEST_LOG -Value 'start' -Encoding ASCII
Write-Output 'fixture-core-started'
exit 0
'@
    $runner = Join-Path $fixture 'run.ps1'
    Set-Content -LiteralPath $runner -Encoding ASCII -Value @'
function global:docker {
    if ($args -contains 'build') { $step = 'build' }
    elseif ($args -contains 'up') { $step = 'pds' }
    elseif ($args -contains 'logs') { $step = 'pds-logs' }
    elseif ($args -contains 'migrate') { $step = 'migrate' }
    elseif ($args -contains 'demo_setup') { $step = 'setup' }
    else { throw ('Unexpected Docker command: ' + ($args -join ' ')) }
    Add-Content -LiteralPath $env:ROBOMORI_DEMO_TEST_LOG -Value $step -Encoding ASCII
    $global:LASTEXITCODE = 0
    if ($step -eq 'pds-logs') { Write-Output 'fixture-pds-health-failed' }
    if ($env:ROBOMORI_DEMO_TEST_FAILURE -eq $step) {
        Write-Output ('fixture-' + $step + '-failed')
        # A command that printed a key but then failed must still stop the launcher.
        if ($step -eq 'setup') { Write-Output '{"kty":"fixture-only"}' }
        $global:LASTEXITCODE = 17
        return
    }
    if ($step -eq 'setup') {
        Write-Output '{"kty":"fixture-only"}'
        Write-Output 'fixture-account-ready'
    }
}
function global:Invoke-WebRequest {
    if ($env:ROBOMORI_DEMO_TEST_FAILURE -eq 'pds-health') { return @{ StatusCode = 503 } }
    return @{ StatusCode = 200 }
}
function global:Start-Process { throw 'No browser, serial relay or external process may be started by this fixture' }
& (Join-Path $PSScriptRoot 'core/host/demo.ps1') -NoRelay -NoViewer -Keyboard -NoPsoc
exit $LASTEXITCODE
'@

    $cases = @(
        @{ Name = 'build'; Exit = 1; Events = @('build') },
        @{ Name = 'pds'; Exit = 1; Events = @('build', 'pds') },
        @{ Name = 'pds-health'; Exit = 1; Events = @('build', 'pds', 'pds-logs') },
        @{ Name = 'migrate'; Exit = 1; Events = @('build', 'pds', 'migrate') },
        @{ Name = 'setup'; Exit = 1; Events = @('build', 'pds', 'migrate', 'setup') },
        @{ Name = 'success'; Exit = 0; Events = @('build', 'pds', 'migrate', 'setup', 'start') }
    )
    foreach ($case in $cases) {
        $env:ROBOMORI_DEMO_TEST_LOG = Join-Path $fixture ($case.Name + '.log')
        $env:ROBOMORI_DEMO_TEST_FAILURE = $case.Name
        $output = @(& $engine -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $runner 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
        $events = @(Get-Content -LiteralPath $env:ROBOMORI_DEMO_TEST_LOG)
        if ($code -ne $case.Exit -or ($events -join ',') -ne ($case.Events -join ',')) {
            throw ('Case {0}: exit {1}, events {2}; output: {3}' -f $case.Name, $code, ($events -join ','), ($output -join "`n"))
        }
        $message = if ($case.Name -eq 'success') { 'fixture-core-started' } else { 'fixture-' + $case.Name + '-failed' }
        if (($output -join "`n") -notmatch [regex]::Escape($message)) {
            throw ('Case {0}: the launcher did not show its result: {1}' -f $case.Name, ($output -join "`n"))
        }
        if ($case.Name -eq 'setup' -and ($output -join "`n") -match 'fixture-only') {
            throw 'The launcher printed a recipient key while reporting a setup failure'
        }
        Write-Host ('PASS: ' + $case.Name)
    }
    Write-Host ('Demo launcher checks passed ({0} cases)' -f $cases.Count)
}
finally {
    $env:ROBOMORI_DEMO_TEST_LOG = $savedLog
    $env:ROBOMORI_DEMO_TEST_FAILURE = $savedFailure
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
