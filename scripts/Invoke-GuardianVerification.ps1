[CmdletBinding()]
param(
  [string]$UnityPath = $env:GUARDIAN_UNITY_PATH
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($UnityPath)) {
  $candidate = 'C:\Program Files\Unity\Hub\Editor\6000.3.12f1\Editor\Unity.exe'
  if (Test-Path -LiteralPath $candidate) { $UnityPath = $candidate }
}
if ([string]::IsNullOrWhiteSpace($UnityPath) -or -not (Test-Path -LiteralPath $UnityPath)) {
  throw 'Set GUARDIAN_UNITY_PATH or -UnityPath to the Unity 6000.3.12f1 executable.'
}

$logDirectory = Join-Path $projectRoot 'Logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory 'guardian-integration.log'
Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
$arguments = @(
  '-batchmode', '-nographics', '-quit',
  '-projectPath', ('"{0}"' -f $projectRoot),
  '-executeMethod', 'Guardian.UnitySim.Editor.GuardianIntegrationBatch.Verify',
  '-logFile', ('"{0}"' -f $logPath)
)

$unityProcess = Start-Process -FilePath $UnityPath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
$unityStatus = $unityProcess.ExitCode
if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath }
if ($unityStatus -ne 0) { throw "Unity integration verification failed with exit code $unityStatus. Log: $logPath" }
if (-not (Test-Path -LiteralPath $logPath)) { throw "Unity did not create its integration log: $logPath" }
if (-not (Select-String -LiteralPath $logPath -SimpleMatch '[Guardian] GUARDIAN_BATCH_OK: integration' -Quiet)) {
  throw 'Unity exited without the Guardian integration success marker.'
}
Write-Host '[Guardian] Licensed Unity integration verification passed.'
