# Serves patch-server\repo to phones on the LAN (http://<this PC's IP>:8790/) for testing the Android updater:
# setup-project.ps1 -PatchRepo http://<IP>:8790/ -PatchChannel android-test, then publish-android.ps1 -Channel android-test.
# Allow the port in the Windows Firewall once (admin): New-NetFirewallRule -DisplayName 'Ro3 patch repo' -Direction Inbound -Protocol TCP -LocalPort 8790 -Action Allow
param(
  [string]$Repo = (Join-Path (Split-Path $PSScriptRoot -Parent) 'patch-server\repo'),
  [int]$Port = 8790,
  # Chunks missing locally come from here (read-only) and are kept: '-' serves the local repository only.
  [string]$Upstream = 'https://patch.herosaga.com.br/'
)
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -in 'Dhcp', 'Manual' -and $_.IPAddress -notmatch '^(127|169\.254|26)\.' } | Select-Object -First 1).IPAddress
Write-Host "phones: http://${ip}:$Port/"
dotnet run (Join-Path $PSScriptRoot 'tools\RepoServer.cs') -- $Repo $Port $Upstream
