# Drives the running port from the login screen into the world: password, "Entrar no jogo", the server screen's
# "Entrar no jogo", the character screen's "Entrar no jogo", and saves a screenshot at each step (out\flow-*.png) plus
# the Unity log (out\flow.log). Each step waits for its marker in logcat instead of a fixed time. Tap positions are for
# the game's mobile UI (the port's default; a pc-ui file in the app's data folder switches to the PC one) on a 2340x1080
# landscape screen (Galaxy S24); the account field keeps the last account, so only the password is typed.
param(
  [string]$Password = 'teste123',
  [int]$Settle = 8,
  [string]$Unity = 'E:\Editors\2022.3.62f3\Editor\Unity.exe'
)
$ErrorActionPreference = 'Continue'
$adb = Join-Path (Split-Path $Unity) 'Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
$out = Join-Path $PSScriptRoot 'out'
$device = (Get-Content (Join-Path $PSScriptRoot 'build\device.txt') -Raw).Trim()
$s = @('-s', $device)

function Shot([string]$name) {
  & $adb @s shell input keyevent KEYCODE_WAKEUP
  & $adb @s shell screencap -p /sdcard/ro3shot.png
  & $adb @s pull /sdcard/ro3shot.png (Join-Path $out "flow-$name.png") 2>&1 | Out-Null
}

# Polls the Unity log (since the app started: run-on-phone.ps1 clears it at launch) for a pattern.
function WaitLog([string]$pattern, [int]$timeout = 120) {
  $t0 = Get-Date
  while (((Get-Date) - $t0).TotalSeconds -lt $timeout) {
    if (& $adb @s logcat -d -v brief Unity:V '*:S' 2>$null | Select-String -Pattern $pattern -Quiet) {
      Write-Host ("  [{0,5:N0}s] {1}" -f ((Get-Date) - $t0).TotalSeconds, $pattern); return $true
    }
    Start-Sleep -Seconds 2
  }
  Write-Host "  timeout waiting for $pattern"; return $false
}

& $adb @s shell input keyevent KEYCODE_WAKEUP
Write-Host '== login screen'
WaitLog 'MapTexture candidate: login_bg_01' | Out-Null
Start-Sleep -Seconds 3
& $adb @s shell input tap 1170 815          # password field (mobile UI layout)
Start-Sleep 2
& $adb @s shell input text $Password
& $adb @s shell input keyevent 66
Start-Sleep 1
& $adb @s shell input tap 1170 910          # Entrar no jogo (login)
Write-Host '== server screen'
WaitLog 'login_icon_server' | Out-Null
Start-Sleep -Seconds 4
Shot 'servers'
& $adb @s shell input tap 1170 877          # Entrar no jogo (server)
Write-Host '== character screen'
if (WaitLog 'SelectRole\]\[Verify\] OnEnter' 60) {
  Start-Sleep -Seconds 3
  Shot 'roles'
  & $adb @s shell input tap 1170 992        # Entrar no jogo (character select)
}
Write-Host '== world'
WaitLog 'OnRequestCompleted: sc_(?!creator)\w+ Load' 120 | Out-Null
Start-Sleep -Seconds $Settle
Shot 'entered'
& $adb @s logcat -d -v brief Unity:V '*:S' 2>&1 | Out-File -Encoding utf8 (Join-Path $out 'flow.log')
Write-Host "flow done: out\flow-servers.png, out\flow-roles.png, out\flow-entered.png, out\flow.log"
