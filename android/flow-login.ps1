# Drives the running port from the login screen into the world: password, "Entrar no jogo", the server screen's
# "Entrar no jogo", the character screen's "Entrar no jogo", and saves a screenshot at each step (out\flow-*.png) plus
# the Unity log (out\flow.log). Each step waits for its marker in logcat instead of a fixed time. Tap positions are for
# the game's mobile UI (the port's default; a pc-ui file in the app's data folder switches to the PC one) on a 2340x1080
# landscape screen (Galaxy S24); the account field keeps the last account, so only the password is typed.
param(
  [string]$Password = 'teste123',
  [int]$Settle = 8,
  [string]$Unity = 'C:\Dev\Unity\Editors\2022.3.62f3\Editor\Unity.exe'
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

# Taps and typing only ever reach the game: with the phone locked, or the game sent to the background, the same
# coordinates land on the home screen and open other apps. Every input checks the focused window first and the flow
# stops (instead of going on blind) when the game is not in front or a step's log marker never came.
function GameInFront {
  $focus = & $adb @s shell dumpsys window 2>$null | Select-String 'mCurrentFocus' | Select-Object -First 1
  return "$focus" -match 'com\.ro3emu\.client/'
}
function Stop-Flow([string]$why) {
  Write-Host "flow stopped: $why (no more input sent; unlock the phone and keep the game in front, then run it again)"
  & $adb @s logcat -d -v brief Unity:V '*:S' 2>&1 | Out-File -Encoding utf8 (Join-Path $out 'flow.log')
  exit 1
}
function Send([string[]]$inputArgs) {
  if (-not (GameInFront)) { Stop-Flow 'the game is not the focused window' }
  & $adb @s shell input @inputArgs
}
function Step([string]$pattern, [int]$timeout = 120) {
  if (-not (WaitLog $pattern $timeout)) { Stop-Flow "no '$pattern' in the log" }
}

& $adb @s shell input keyevent KEYCODE_WAKEUP
Write-Host '== login screen'
Step 'MapTexture candidate: login_bg_01'
Start-Sleep -Seconds 3
Send tap, 1170, 815                         # password field (mobile UI layout)
Start-Sleep 2
Send text, $Password
Send keyevent, 66
Start-Sleep 1
Send tap, 1170, 910                         # Entrar no jogo (login)
Write-Host '== server screen'
Step 'login_icon_server'
Start-Sleep -Seconds 4
Shot 'servers'
Send tap, 1170, 877                         # Entrar no jogo (server)
Write-Host '== character screen'
# The scene load (C#), not Lua's SelectRole log: the port keeps Lua logging off (Hooks.Platform.LuaLog).
Step 'OnBundleLoadCompleted: sc_creator' 60
Start-Sleep -Seconds 6
Shot 'roles'
Send tap, 1170, 992                         # Entrar no jogo (character select)
Write-Host '== world'
Step 'OnRequestCompleted: sc_(?!creator)\w+ Load' 120
Start-Sleep -Seconds $Settle
Shot 'entered'
& $adb @s logcat -d -v brief Unity:V '*:S' 2>&1 | Out-File -Encoding utf8 (Join-Path $out 'flow.log')
Write-Host "flow done: out\flow-servers.png, out\flow-roles.png, out\flow-entered.png, out\flow.log"
