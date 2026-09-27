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

    $bridgePath = Join-Path $extractRoot "AgentHost\SolidWorksCadAgent.SolidWorksBridge.dll"
    $bridgeAssembly = [System.Reflection.Assembly]::LoadFrom($bridgePath)
    $capabilitiesType = $bridgeAssembly.GetType(
        "SolidWorksCadAgent.SolidWorksBridge.BridgeBuildCapabilities",
        $true)
    $capabilityField = $capabilitiesType.GetField(
        "Capability",
        [System.Reflection.BindingFlags]"Public,Static")
    $compiledCapability = [string]$capabilityField.GetRawConstantValue()
    if ($compiledCapability -ne $capability) {
        throw "Bundle capability marker '$capability' does not match compiled bridge capability '$compiledCapability'."
    }

    if ($capability -eq "NativeSolidWorksInterop") {
        $nativeDependencies = @(
            "AgentHost\SolidWorks.Interop.sldworks.dll",
            "AgentHost\SolidWorks.Interop.swconst.dll"
        )
        $missingNativeDependencies = @(
            foreach ($relativePath in $nativeDependencies) {
                if (-not (Test-Path -LiteralPath (Join-Path $extractRoot $relativePath) -PathType Leaf)) {
                    $relativePath
                }
            }
        )
        if ($missingNativeDependencies.Count -gt 0) {
            throw "Native bundle is missing SOLIDWORKS interop dependencies: $($missingNativeDependencies -join ', ')"
        }
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
