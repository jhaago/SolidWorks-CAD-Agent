param(
    [string]$User = "$env:USERDOMAIN\$env:USERNAME"
)

$ErrorActionPreference = "Stop"
$url = "http://127.0.0.1:53741/"

# Run this script from an elevated PowerShell prompt once per Windows user.
netsh http add urlacl url=$url user="$User"

# To remove the reservation later, run:
# netsh http delete urlacl url=http://127.0.0.1:53741/
