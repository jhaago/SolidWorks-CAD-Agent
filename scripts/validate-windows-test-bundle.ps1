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
        "RemoteAgent\SolidWorksCadAgent.RemoteAgent.exe",
        "RemoteAgent\SolidWorksCadAgent.RemoteAgent.exe.config",
        "RemoteAgent\SolidWorksCadAgent.Contracts.dll",
        "RemoteAgent\SolidWorksCadAgent.Core.dll",
        "RemoteAgent\Newtonsoft.Json.dll",
        "Setup\register-agent-host-url.ps1",
        "Setup\register-remote-agent-url.ps1",
        "LIVE-REMOTE.txt",
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
    $remoteScript = Get-Content -LiteralPath (Join-Path $extractRoot "Setup\register-remote-agent-url.ps1") -Raw
    if ($remoteScript -notmatch 'http://127\.0\.0\.1:5079/' -or $remoteScript -match 'http://[+*]') {
        throw "Remote setup must reserve the fixed loopback prefix only."
    }
    $credentials = @(Get-ChildItem $extractRoot -Recurse -File | Where-Object { $_.Name -match '^(devices\.dat|remote-workstation-.*\.enc)$' })
    if ($credentials.Count -gt 0) { throw "Windows test bundle contains device credentials." }

    $capability = (Get-Content -LiteralPath (Join-Path $extractRoot "BUILD-CAPABILITY.txt") -Raw).Trim()
    $allowedCapabilities = @(
        "NativeSolidWorksInterop",
        "CompileOnlyNoSolidWorksInterop"
    )
    if ($capability -notin $allowedCapabilities) {
        throw "Windows test bundle has an unknown bridge capability: $capability"
    }

    $bridgePath = Join-Path $extractRoot "AgentHost\SolidWorksCadAgent.SolidWorksBridge.dll"
    $capabilityReaderPath = Join-Path $PSScriptRoot "read-bridge-capability.ps1"
    $powerShellPath = (Get-Process -Id $PID).Path
    $capabilityOutput = @(
        & $powerShellPath -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
            -File $capabilityReaderPath -BridgePath $bridgePath 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect compiled bridge capability: $($capabilityOutput -join [Environment]::NewLine)"
    }
    $compiledCapability = ([string]($capabilityOutput | Select-Object -Last 1)).Trim()
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
