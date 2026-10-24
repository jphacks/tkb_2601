param(
  [Parameter(Mandatory = $true)][string]$ProjectPath,
  [string]$UnityExe = "",
  [switch]$OpenAfter
)

$ErrorActionPreference = 'Stop'
$version = '6000.3.12f1'
$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$projectVersionFile = Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt'
if (-not (Test-Path -LiteralPath $projectVersionFile)) {
  throw "Not a Unity project: $ProjectPath"
}
if ((Get-Content -LiteralPath $projectVersionFile -Raw) -notmatch 'm_EditorVersion:\s*6000\.3\.12f1') {
  throw "Unity version must be exactly $version"
}
if (-not $UnityExe) { $UnityExe = $env:UNITY_EDITOR_EXE }
if (-not $UnityExe) {
  $paths = @(
    (Join-Path $env:ProgramFiles "Unity\Hub\Editor\$version\Editor\Unity.exe"),
    (Join-Path $env:ProgramFiles "Unity Hub\Editor\$version\Editor\Unity.exe")
  )
  foreach ($path in $paths) {
    if (Test-Path -LiteralPath $path) { $UnityExe = $path; break }
  }
}
if (-not $UnityExe -or -not (Test-Path -LiteralPath $UnityExe)) {
  throw "Unity $version executable not found. Set Windows UNITY_EDITOR_EXE to Unity.exe path."
}

$logDir = Join-Path $ProjectPath 'Logs'
New-Item -Path $logDir -ItemType Directory -Force | Out-Null
$log = Join-Path $logDir 'GuardianNekobotBatch.log'
$scene = Join-Path $ProjectPath 'Assets\Scenes\GuardianRestaurant.unity'

function Show-BatchLog {
  if (Test-Path -LiteralPath $log) {
    Write-Host '[Guardian] Last 80 lines of Unity log:'
    Get-Content -LiteralPath $log -Tail 80
  } else {
    Write-Host "[Guardian] No Unity log was created at $log"
  }
}

# Unity batchmode cannot open a project already held by a GUI Editor.
# Preflight checks Windows process command lines and never forcibly kills
# an editor that may contain unsaved scene changes.
$activeEditors = @()
try {
  $normalizedProject = $ProjectPath.Replace([char]47, [char]92).TrimEnd([char]92)
  $activeEditors = @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction Stop |
    Where-Object {
      $commandLine = [string]$_.CommandLine
      $normalizedCommandLine = $commandLine.Replace([char]47, [char]92)
      $commandLine -match '(?i)-projectPath' -and
        $normalizedCommandLine.IndexOf($normalizedProject,
          [StringComparison]::OrdinalIgnoreCase) -ge 0
    })
} catch {
  Write-Warning "[Guardian] Could not inspect Unity process command lines: $($_.Exception.Message). Proceeding; batchmode will still verify its result."
}
if ($activeEditors.Count -gt 0) {
  Write-Host '[Guardian] BLOCKED: This project is already open in a Unity Editor.' -ForegroundColor Yellow
  foreach ($editor in $activeEditors) {
    Write-Host "[Guardian] Active Unity.exe PID: $($editor.ProcessId)"
  }
  Write-Host '[Guardian] Save your scene in Unity, close the Editor, then rerun apply-nekobot.sh.' -ForegroundColor Yellow
  Write-Host '[Guardian] Nothing was deleted, and this script did not terminate Unity.'
  exit 2
}

# Do not accept a success marker from a previous run.
if (Test-Path -LiteralPath $log) {
  Remove-Item -LiteralPath $log -Force
}

Write-Host "[Guardian] Unity: $UnityExe"
Write-Host "[Guardian] Scene: Assets/Scenes/GuardianRestaurant.unity"
Write-Host '[Guardian] Close all Unity Editors for this project before running batchmode.'
Write-Host '[Guardian] Waiting for Unity batch process to finish (compilation may take several minutes)...'

# Windows PowerShell may return immediately when invoking GUI executables with "&".
# Start-Process -Wait -PassThru reliably waits and exposes a process exit code.
$arguments = @(
  '-batchmode',
  '-nographics',
  '-quit',
  '-projectPath', ('"' + $ProjectPath + '"'),
  '-executeMethod', 'Guardian.UnitySim.Editor.GuardianNekobotBatch.Configure',
  '-logFile', ('"' + $log + '"')
)
try {
  $unityProcess = Start-Process -FilePath $UnityExe -ArgumentList $arguments -PassThru -Wait
  $code = $unityProcess.ExitCode
} catch {
  Write-Host "[Guardian] Failed to launch/wait for Unity: $($_.Exception.Message)" -ForegroundColor Red
  Show-BatchLog
  exit 1
}
if ($null -eq $code) {
  Write-Host '[Guardian] Unity returned no process exit code; treating as failure.' -ForegroundColor Red
  Show-BatchLog
  exit 1
}
if ($code -ne 0) {
  Write-Host "[Guardian] Unity batch failed (exit code $code). Check $log" -ForegroundColor Red
  if (Test-Path -LiteralPath (Join-Path $ProjectPath 'Library\UnityLockfile')) {
    Write-Host '[Guardian] A UnityLockfile exists. This alone does not prove an active Editor.' -ForegroundColor Yellow
    Write-Host '[Guardian] If startup ended immediately, check that no Unity Editor is still using this project.'
    Write-Host '[Guardian] Do not delete the lockfile while any Unity process is using the project.'
  }
  Show-BatchLog
  exit $code
}
if (-not (Test-Path -LiteralPath $log) -or
    -not (Select-String -LiteralPath $log -Pattern 'GUARDIAN_NEKOBOT_BATCH_OK' -Quiet)) {
  Write-Host "[Guardian] Unity finished without the 3-robot success marker. Check $log" -ForegroundColor Red
  Show-BatchLog
  exit 1
}
if (-not (Test-Path -LiteralPath $scene)) {
  Write-Host "[Guardian] Unity success marker found but scene file is missing: $scene" -ForegroundColor Red
  Show-BatchLog
  exit 1
}

Write-Host '[Guardian] SUCCESS: all 3 Nekobot visuals applied and scene saved.' -ForegroundColor Green
Write-Host "[Guardian] Batch log: $log"
if ($OpenAfter) {
  Write-Host '[Guardian] Opening the Unity Editor...'
  Start-Process -FilePath $UnityExe -ArgumentList @('-projectPath', ('"' + $ProjectPath + '"')) | Out-Null
}
exit 0
