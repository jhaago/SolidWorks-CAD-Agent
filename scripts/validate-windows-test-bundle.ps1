[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    throw "Windows test bundle was not created: $ZipPath"
}

$extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "SolidWorksCadAgent-BundleValidation-" + [Guid]::NewGuid().ToString("N"))

try {
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $extractRoot -Force

    $requiredPaths = @(
        "AgentHost\SolidWorksCadAgent.AgentHost.exe",
        "AgentHost\SolidWorksCadAgent.Contracts.dll",
        "AgentHost\SolidWorksCadAgent.Core.dll",
        "AgentHost\SolidWorksCadAgent.SolidWorksBridge.dll",
        "AgentHost\Newtonsoft.Json.dll",
        "AgentHost\System.Data.SQLite.dll",
        "AgentHost\x86\SQLite.Interop.dll",
        "AgentHost\x64\SQLite.Interop.dll",
        "Desktop\SolidWorksCadAgent.Desktop.exe",
        "Desktop\Newtonsoft.Json.dll",
        "Setup\register-agent-host-url.ps1",
        "BUILD-CAPABILITY.txt",
        "START-HERE.txt"
    )

    $missing = @(
        foreach ($relativePath in $requiredPaths) {
            if (-not (Test-Path -LiteralPath (Join-Path $extractRoot $relativePath) -PathType Leaf)) {
                $relativePath
            }
        }
    )

    if ($missing.Count -gt 0) {
        throw "Windows test bundle is missing required files: $($missing -join ', ')"
    }

    $capability = (Get-Content -LiteralPath (Join-Path $extractRoot "BUILD-CAPABILITY.txt") -Raw).Trim()
    $allowedCapabilities = @(
        "NativeSolidWorksInterop",
        "CompileOnlyNoSolidWorksInterop"
    )
    if ($capability -notin $allowedCapabilities) {
        throw "Windows test bundle has an unknown bridge capability: $capability"
    }

    $symbols = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Filter "*.pdb")
    if ($symbols.Count -gt 0) {
        throw "Windows test bundle contains debug symbols: $($symbols.FullName -join ', ')"
    }

    Write-Host "Windows test bundle validation passed: $ZipPath"
}
finally {
    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
}
