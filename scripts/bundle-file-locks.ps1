function Get-CadAgentProcesses {
    @(Get-Process -Name "SolidWorksCadAgent.AgentHost", "SolidWorksCadAgent.Desktop" -ErrorAction SilentlyContinue)
}

function Get-BundleFileAccessMessage {
    param([string]$Path)
    $running = @(Get-CadAgentProcesses | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" })
    $details = if ($running.Count -gt 0) { " Running: " + ($running -join ", ") + "." } else { "" }
    "Cannot replace or remove '$Path'. It may be locked by a previous Agent Host/Desktop session, or access permissions may prevent writing. Please close SolidWorksCadAgent.AgentHost.exe and SolidWorksCadAgent.Desktop.exe, then rerun the bundle build. Also close any test runner using this output and check folder permissions if the error persists.$details Processes were not terminated automatically."
}

function Assert-BundleFilesAvailable {
    param([string[]]$Roots)
    $processes = @(Get-CadAgentProcesses)
    foreach ($root in $Roots | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $root -Filter "SQLite.Interop.dll" -Recurse -File) {
            # A loaded image can prevent replacement even when its file handle has been closed.
            foreach ($process in $processes) {
                $loaded = $false
                try {
                    $loaded = @($process.Modules | Where-Object { $_.FileName -eq $file.FullName }).Count -gt 0
                }
                catch { } # Module enumeration can be restricted; retain the file-access check below.
                if ($loaded) { throw (Get-BundleFileAccessMessage $file.FullName) }
            }
            $stream = $null
            try {
                $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            }
            catch [System.IO.IOException] { throw (Get-BundleFileAccessMessage $file.FullName) }
            catch [System.UnauthorizedAccessException] { throw (Get-BundleFileAccessMessage $file.FullName) }
            finally { if ($null -ne $stream) { $stream.Dispose() } }
        }
    }
}
