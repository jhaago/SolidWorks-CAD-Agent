[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory,
    [string]$SolidWorksApiPath
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "bundle-file-locks.ps1")

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts"
}
else {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}

function Test-SolidWorksApiPath {
    param([string]$Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return $false
    }

    return (
        (Test-Path -LiteralPath (Join-Path $Candidate "SolidWorks.Interop.sldworks.dll") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Candidate "SolidWorks.Interop.swconst.dll") -PathType Leaf)
    )
}

function Find-SolidWorksApiPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-SolidWorksApiPath $resolved)) {
            throw "SolidWorksApiPath does not contain both required SOLIDWORKS interop assemblies: $resolved"
        }
        return $resolved
    }

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramW6432)) {
        $candidates += Join-Path $env:ProgramW6432 "SOLIDWORKS Corp\SOLIDWORKS\api\redist"
    }
    $candidates += "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-SolidWorksApiPath $candidate) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Invoke-MSBuildChecked {
    param([string[]]$Arguments)

    & msbuild @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE. If the output reports access denied or a file in use, close SolidWorksCadAgent.AgentHost.exe, SolidWorksCadAgent.Desktop.exe and SolidWorksCadAgent.RemoteAgent.exe and retry. Processes were not terminated automatically."
    }
}

function Copy-RuntimeTree {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Build output directory does not exist: $Source"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $allowedExtensions = @(".exe", ".dll", ".config")
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        if ($file.Extension -notin $allowedExtensions) {
            continue
        }

        $relativePath = $file.FullName.Substring($Source.Length).TrimStart([char]"\")
        $destinationPath = Join-Path $Destination $relativePath
        $destinationParent = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        try { Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force }
        catch { throw (Get-BundleFileAccessMessage $destinationPath) }
    }
}

$detectedApiPath = Find-SolidWorksApiPath $SolidWorksApiPath
$msbuildProperty = @()
$capability = "CompileOnlyNoSolidWorksInterop"
if ($null -ne $detectedApiPath) {
    $msbuildProperty = @("/p:SolidWorksApiPath=$detectedApiPath")
    $capability = "NativeSolidWorksInterop"
}

$bundleRoot = Join-Path $OutputDirectory "SolidWorksCadAgent-Windows-Test-Bundle"
$zipPath = Join-Path $OutputDirectory "SolidWorksCadAgent-Windows-Test-Bundle.zip"
Assert-BundleFilesAvailable -Roots @(
    (Join-Path $repoRoot "src/SolidWorksCadAgent.AgentHost/bin"),
    (Join-Path $repoRoot "src/SolidWorksCadAgent.Desktop/bin"),
    (Join-Path $repoRoot "src/SolidWorksCadAgent.RemoteAgent/bin"),
    (Join-Path $repoRoot "tests/SolidWorksCadAgent.UnitTests/bin"),
    (Join-Path $repoRoot "tests/SolidWorksCadAgent.IntegrationTests/bin"),
    $bundleRoot
)

Push-Location $repoRoot
try {
    Invoke-MSBuildChecked (@("SolidWorksCadAgent.sln", "/t:Restore", "/p:Configuration=$Configuration") + $msbuildProperty)
    Invoke-MSBuildChecked (@("SolidWorksCadAgent.sln", "/p:Configuration=$Configuration", "/p:Restore=false") + $msbuildProperty)
}
finally {
    Pop-Location
}

if (Test-Path -LiteralPath $bundleRoot) {
    try { Remove-Item -LiteralPath $bundleRoot -Recurse -Force }
    catch { throw (Get-BundleFileAccessMessage $bundleRoot) }
}
if (Test-Path -LiteralPath $zipPath) {
    try { Remove-Item -LiteralPath $zipPath -Force }
    catch { throw (Get-BundleFileAccessMessage $zipPath) }
}

New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null
Copy-RuntimeTree (Join-Path $repoRoot "src\SolidWorksCadAgent.AgentHost\bin\$Configuration\net48") (Join-Path $bundleRoot "AgentHost")
Copy-RuntimeTree (Join-Path $repoRoot "src\SolidWorksCadAgent.Desktop\bin\$Configuration\net48") (Join-Path $bundleRoot "Desktop")
Copy-RuntimeTree (Join-Path $repoRoot "src\SolidWorksCadAgent.RemoteAgent\bin\$Configuration\net48") (Join-Path $bundleRoot "RemoteAgent")

$setupDirectory = Join-Path $bundleRoot "Setup"
New-Item -ItemType Directory -Path $setupDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\register-agent-host-url.ps1") -Destination (Join-Path $setupDirectory "register-agent-host-url.ps1") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\register-remote-agent-url.ps1") -Destination (Join-Path $setupDirectory "register-remote-agent-url.ps1") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\windows-test-bundle-start-here.txt") -Destination (Join-Path $bundleRoot "START-HERE.txt") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\live-remote-windows-testing.md") -Destination (Join-Path $bundleRoot "LIVE-REMOTE.txt") -Force

Set-Content -LiteralPath (Join-Path $bundleRoot "BUILD-CAPABILITY.txt") -Value $capability -Encoding ASCII

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Compress-Archive -Path (Join-Path $bundleRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Created Windows test bundle: $zipPath"
Write-Host "Bridge capability: $capability"
if ($capability -eq "CompileOnlyNoSolidWorksInterop") {
    Write-Warning "This bundle supports Simulation mode only. Build on a SOLIDWORKS PC for native COM support."
}
