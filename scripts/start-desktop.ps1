[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$Build)
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Build) {
    Push-Location $repoRoot
    try {
        & dotnet build SolidWorksCadAgent.sln -c $Configuration -m:1 -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw 'Build failed. Resolve the build errors before launching.' }
    } finally { Pop-Location }
}
$hostPath = Join-Path $repoRoot "src\SolidWorksCadAgent.AgentHost\bin\$Configuration\net48\SolidWorksCadAgent.AgentHost.exe"
$desktopPath = Join-Path $repoRoot "src\SolidWorksCadAgent.Desktop\bin\$Configuration\net48\SolidWorksCadAgent.Desktop.exe"
if (Test-Path -LiteralPath (Join-Path $repoRoot 'AgentHost\SolidWorksCadAgent.AgentHost.exe')) {
    $hostPath = Join-Path $repoRoot 'AgentHost\SolidWorksCadAgent.AgentHost.exe'
    $desktopPath = Join-Path $repoRoot 'Desktop\SolidWorksCadAgent.Desktop.exe'
}
foreach ($runtimePath in @($hostPath, $desktopPath)) {
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) { throw "Missing output: $runtimePath. Run this script with -Build first." }
}
function Test-AgentHost {
    try { $health = Invoke-RestMethod -Uri 'http://127.0.0.1:53741/health' -TimeoutSec 1; return $health.status -eq 'ok' }
    catch { return $false }
}
if (-not (Test-AgentHost)) {
    $logDirectory = Join-Path $repoRoot 'artifacts\host-logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $runId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $hostProcess = Start-Process -FilePath $hostPath -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logDirectory "$runId.stdout.log") `
        -RedirectStandardError (Join-Path $logDirectory "$runId.stderr.log")
    $deadline = (Get-Date).AddSeconds(12)
    while (-not (Test-AgentHost)) {
        if ($hostProcess.HasExited -or (Get-Date) -ge $deadline) { throw "Host did not become ready. Check $logDirectory. First-time URL setup is in README.md." }
        Start-Sleep -Milliseconds 300
    }
}
# This is the interactive application the user launched; show its window.
Start-Process -FilePath $desktopPath -WorkingDirectory $repoRoot
