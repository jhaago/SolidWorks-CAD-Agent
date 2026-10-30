[CmdletBinding()]
param()
# Build-only reference acquisition: no NuGet install hooks, project dependency or binary redistribution.
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$msbuild = (Get-Command msbuild -ErrorAction Stop).Source
$root = Join-Path ([IO.Path]::GetTempPath()) ('SolidWorksCadAgent-2020Compile-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $package = Join-Path $root 'references.nupkg'
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/visiativ.solidworks.interop/28.1.0/visiativ.solidworks.interop.28.1.0.nupkg' -OutFile $package
    $expected = '828d3639aec02ca8219db1ac24e5857d0c7a485282fd0a682e4193f76ca547d6'
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Pinned 2020 reference package hash differs; refusing to use it.' }
    $references = Join-Path $root 'references'; New-Item -ItemType Directory -Path $references | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        foreach ($name in @('SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll')) {
            $entry = $archive.GetEntry('lib/net48/' + $name)
            if ($null -eq $entry) { throw "Missing pinned reference $name" }
            $path = Join-Path $references $name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path)
            $identity = [Reflection.AssemblyName]::GetAssemblyName($path)
            if ($identity.Version.ToString() -ne '28.1.0.74') { throw "Wrong reference version for $name" }
        }
    }
    finally { $archive.Dispose() }
    $output = (Join-Path $root 'compiled') + [IO.Path]::DirectorySeparatorChar
    & $msbuild (Join-Path $repository 'SolidWorksCadAgent.sln') /t:Build /p:Configuration=Debug /p:Restore=false "/p:SolidWorksApiPath=$references" /p:SolidWorksInteropAvailable=true "/p:OutputPath=$output" /m:1 /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native-enabled SOLIDWORKS 2020 API compilation failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $output 'SolidWorksCadAgent.SolidWorksBridge.dll'))) { throw 'Native-enabled bridge output is missing.' }
    Write-Host 'Native-enabled solution compiled against version 28.1.0.74 references. SOLIDWORKS runtime not executed; binaries remain outside distributed artifacts.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
