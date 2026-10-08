[CmdletBinding()]
param([string]$User = "$env:USERDOMAIN\$env:USERNAME")

$ErrorActionPreference = "Stop"
$url = "http://127.0.0.1:5079/"
# Run once in elevated PowerShell. If elevated under another account, pass -User
# with the ordinary Windows account that will run RemoteAgent.
& netsh http add urlacl "url=$url" "user=$User"
if ($LASTEXITCODE -ne 0) {
    throw "Could not reserve $url for $User. Use an Administrator PowerShell prompt. If this reservation already exists, inspect it with 'netsh http show urlacl url=$url'; do not replace another user's reservation automatically."
}
Write-Host "Reserved $url for $User. Run RemoteAgent as this ordinary user."
# Optional removal, in elevated PowerShell:
# netsh http delete urlacl url=http://127.0.0.1:5079/
