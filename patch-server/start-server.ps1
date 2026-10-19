# Hero Saga - Inicia o servidor local de patches
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$serverScript = Join-Path $PSScriptRoot "server.py"
$repoDir = Join-Path $PSScriptRoot "repo"

Write-Host "Iniciando Servidor de Patches Hero Saga..." -ForegroundColor Cyan

# Verifica se o repositório já foi inicializado
if (-not (Test-Path (Join-Path $repoDir "channels/test.json"))) {
    Write-Host "Inicializando repositório de patches..." -ForegroundColor Yellow
    & dotnet run --project (Join-Path $root "src/Ro3Emu.ClientTools") -- init-repo $repoDir test 1.0.0
}

# Tenta rodar via Python
if (Get-Command python -ErrorAction SilentlyContinue) {
    & python $serverScript
} else {
    Write-Host "Python não encontrado no PATH. Usando servidor HTTP embutido do PowerShell..." -ForegroundColor Yellow
    $listener = New-Object System.Net.HttpListener
    $prefix = "http://localhost:8790/"
    $listener.Prefixes.Add($prefix)
    $listener.Start()
    Write-Host "Servidor de patches rodando em $prefix" -ForegroundColor Green
    Write-Host "Pressione Ctrl+C para parar.`n" -ForegroundColor Gray

    try {
        while ($listener.IsListening) {
            $context = $listener.GetContext()
            $request = $context.Request
            $response = $context.Response
            
            $urlPath = $request.Url.LocalPath.TrimStart('/')
            if ([string]::IsNullOrWhiteSpace($urlPath)) { $urlPath = "channels/test.json" }
            $filePath = Join-Path $repoDir ($urlPath -replace '/', '\')
            
            $response.Headers.Add("Access-Control-Allow-Origin", "*")
            
            if (Test-Path $filePath -PathType Leaf) {
                $bytes = [System.IO.File]::ReadAllBytes($filePath)
                $response.StatusCode = 200
                $ext = [System.IO.Path]::GetExtension($filePath).ToLower()
                $contentType = switch ($ext) {
                    ".json" { "application/json; charset=utf-8" }
                    ".zst"  { "application/zstd" }
                    ".exe"  { "application/vnd.microsoft.portable-executable" }
                    default { "application/octet-stream" }
                }
                $response.ContentType = $contentType
                $response.ContentLength64 = $bytes.Length
                $response.OutputStream.Write($bytes, 0, $bytes.Length)
                Write-Host "[$([DateTime]::Now.ToString('HH:mm:ss'))] 200 GET $urlPath" -ForegroundColor Green
            } else {
                $response.StatusCode = 404
                $msg = [System.Text.Encoding]::UTF8.GetBytes("Not Found")
                $response.OutputStream.Write($msg, 0, $msg.Length)
                Write-Host "[$([DateTime]::Now.ToString('HH:mm:ss'))] 404 GET $urlPath" -ForegroundColor Red
            }
            $response.Close()
        }
    } finally {
        $listener.Stop()
    }
}
