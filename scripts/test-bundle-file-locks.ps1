$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "bundle-file-locks.ps1")

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("CadAgent-Locks-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $root | Out-Null
$path = Join-Path $root "SQLite.Interop.dll"
[System.IO.File]::WriteAllText($path, "test fixture")
try {
    Assert-BundleFilesAvailable -Roots @($root)
    $handle = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $failure = $null
        try { Assert-BundleFilesAvailable -Roots @($root) }
        catch { $failure = $_.Exception.Message }
        if ($null -eq $failure) { throw "Expected a locked DLL to fail the build preflight." }
        foreach ($required in @("SQLite.Interop.dll", "SolidWorksCadAgent.AgentHost", "SolidWorksCadAgent.Desktop", "SolidWorksCadAgent.RemoteAgent", "close", "not terminated")) {
            if ($failure -notlike "*$required*") { throw "Missing actionable diagnostic: $required" }
        }
    }
    finally { $handle.Dispose() }
    Assert-BundleFilesAvailable -Roots @($root)
    Write-Host "Bundle file-lock regression tests passed."
}
finally { Remove-Item -LiteralPath $root -Recurse -Force }
