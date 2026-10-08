[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BridgePath
)

$ErrorActionPreference = "Stop"

$bridgeAssembly = [System.Reflection.Assembly]::LoadFrom($BridgePath)
$capabilitiesType = $bridgeAssembly.GetType(
    "SolidWorksCadAgent.SolidWorksBridge.BridgeBuildCapabilities",
    $true)
$capabilityField = $capabilitiesType.GetField(
    "Capability",
    [System.Reflection.BindingFlags]"Public,Static")

Write-Output ([string]$capabilityField.GetRawConstantValue())
