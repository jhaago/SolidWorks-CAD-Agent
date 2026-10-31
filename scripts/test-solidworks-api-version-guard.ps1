[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository 'src/SolidWorksCadAgent.SolidWorksBridge/SolidWorksCadAgent.SolidWorksBridge.csproj'
$msbuild = (Get-Command msbuild -ErrorAction Stop).Source
$root = Join-Path ([IO.Path]::GetTempPath()) ('SolidWorksCadAgent-ApiGuard-' + [Guid]::NewGuid().ToString('N'))

function Write-ReferenceFixture([string]$directory, [string]$sldVersion, [string]$constVersion) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($pair in @(@('sldworks', $sldVersion), @('swconst', $constVersion))) {
        $typeName = 'ApiGuardFixture_' + [Guid]::NewGuid().ToString('N')
        $source = 'using System.Reflection; [assembly: AssemblyVersion("' + $pair[1] + '")] public class ' + $typeName + ' {}'
        Add-Type -TypeDefinition $source -OutputAssembly (Join-Path $directory ('SolidWorks.Interop.' + $pair[0] + '.dll'))
    }
}
function Assert-Guard([string]$directory, [bool]$success, [string]$errorCode) {
    $log = (& $msbuild $project /t:ValidateSolidWorks2020Interop "/p:SolidWorksApiPath=$directory" /nologo /v:minimal 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    if ($success) { if ($exitCode -ne 0) { throw "Matching metadata fixture was rejected: $log" } }
    elseif ($exitCode -eq 0 -or $log -notmatch $errorCode) { throw "Expected $errorCode, got exit $exitCode`: $log" }
}
try {
    $good = Join-Path $root 'good'; Write-ReferenceFixture $good '28.1.0.74' '28.1.0.74'; Assert-Guard $good $true ''
    $newer = Join-Path $root 'newer'; Write-ReferenceFixture $newer '32.1.0.0' '32.1.0.0'; Assert-Guard $newer $true ''
    $older = Join-Path $root 'older'; Write-ReferenceFixture $older '27.3.0.0' '27.3.0.0'; Assert-Guard $older $true ''
    $mixed = Join-Path $root 'mixed'; Write-ReferenceFixture $mixed '28.1.0.74' '28.2.0.0'; Assert-Guard $mixed $false 'SW2020_API_MISMATCH'
    $missing = Join-Path $root 'missing'; New-Item -ItemType Directory -Path $missing -Force | Out-Null; Assert-Guard $missing $false 'SW2020_API_MISSING'
    Write-Host 'API guard: 5 metadata cases passed. Synthetic fixtures validate presence and matching metadata only, not native API signatures.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
# Expected failed probes must not fail GitHub's appended pwsh LASTEXITCODE check.
# An assertion or cleanup exception propagates before reaching this success assignment.
$global:LASTEXITCODE = 0
