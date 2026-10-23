<#
.SYNOPSIS
  Hero Saga - atualiza o servidor da VPS sem compilar la.

.DESCRIPTION
  Compila o servidor aqui (linux-x64, framework-dependent, como o da VPS), envia o pacote por SSH e roda
  tools/vps/install.sh, que para o servico, guarda o servidor anterior em bin/Release.prev, instala o novo e
  sobe o servico systemd "ro3emu" (reinicia sozinho se cair e sobrevive ao fim da sessao SSH).
  Precisa da chave %USERPROFILE%\.ssh\herosaga_vps. Faca "git pull" na VPS antes, se quiser o codigo-fonte la.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\vps\deploy.ps1
#>
param(
    [string]$HostName = 'root@45.130.137.89',
    [string]$Key = (Join-Path $env:USERPROFILE '.ssh\herosaga_vps')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:PATH += ';C:\Program Files\dotnet'
$out = Join-Path ([IO.Path]::GetTempPath()) 'ro3emu-vps-publish'
$pkg = Join-Path ([IO.Path]::GetTempPath()) 'ro3emu-server-linux.tar.gz'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

Write-Host '[1/3] Compilando o servidor para Linux...'
& dotnet publish (Join-Path $root 'src\Ro3Emu.Server') -c Release -r linux-x64 --self-contained false -nologo -v q `
    -p:IsTransformWebConfigDisabled=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'falha no build' }
Get-ChildItem $out -Filter '*.pdb' | Remove-Item
& tar -czf $pkg -C $out .
if ($LASTEXITCODE -ne 0) { throw 'falha ao empacotar' }

Write-Host '[2/3] Enviando para a VPS...'
$ssh = @('-i', $Key, '-o', 'BatchMode=yes')
& scp @ssh $pkg (Join-Path $PSScriptRoot 'install.sh') (Join-Path $PSScriptRoot 'ro3emu.service') "${HostName}:/tmp/"
if ($LASTEXITCODE -ne 0) { throw 'falha no envio' }

Write-Host '[3/3] Instalando e iniciando...'
& ssh @ssh $HostName "sed -i 's/\r$//' /tmp/install.sh /tmp/ro3emu.service && bash /tmp/install.sh /tmp/ro3emu-server-linux.tar.gz"
if ($LASTEXITCODE -ne 0) { throw 'falha na instalacao' }
Write-Host 'PRONTO. Log: ssh na VPS e "tail -f /opt/ro3emu-server/logs/ro3emu.log"; status: "systemctl status ro3emu".'
