<#
.SYNOPSIS
  Compila o Mission4Me e gera o instalador.

.EXAMPLE
  .\build.ps1                 # versão 1.0.0, gera dist\Mission4Me-Setup-1.0.0.exe
  .\build.ps1 -Version 1.2.0  # versão específica (o GitHub Actions passa a da tag)
  .\build.ps1 -Dev            # build rápido para testar (precisa do .NET 8), abre o app
#>
param(
    [string]$Version = "1.0.0",
    [switch]$Dev
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$project = "src\Mission4Me\Mission4Me.csproj"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versão inválida '$Version' (use X.Y.Z)" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK não encontrado. Instale com: winget install Microsoft.DotNet.SDK.8"
}

# --- fecha instâncias abertas de forma limpa (devolve a cor da barra e guarda a missão)
foreach ($name in @("Mission4Me")) {
    $procs = Get-Process $name -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Host "Fechando $name em execução..."
        $exe = $procs[0].Path
        if ($exe) { Start-Process $exe -ArgumentList "--sair" -Wait -ErrorAction SilentlyContinue }
        $procs | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
        Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

if ($Dev) {
    # dependente do .NET instalado: compila rápido, só para testar
    dotnet publish $project -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:Version=$Version -o dist\dev
    if ($LASTEXITCODE) { throw "Falha na compilação" }
    Start-Process "dist\dev\Mission4Me.exe"
    return
}

# --- 1) executável único self-contained (não exige .NET no PC do usuário)
Write-Host "`n== Compilando Mission4Me $Version ==" -ForegroundColor Cyan
if (Test-Path dist\app) { Remove-Item dist\app -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -o dist\app
if ($LASTEXITCODE) { throw "Falha na compilação" }

# --- 2) instalador (Inno Setup 6)
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup 6 não encontrado: o executável está em dist\app, mas o instalador não foi gerado."
    Write-Warning "Instale com: winget install JRSoftware.InnoSetup  (e rode este script de novo)"
    return
}

Write-Host "`n== Gerando instalador ==" -ForegroundColor Cyan
& $iscc /Qp "/DAppVersion=$Version" installer\Mission4Me.iss
if ($LASTEXITCODE) { throw "Falha no Inno Setup" }

# --- 3) checksum para quem baixar conferir a integridade
$setup = "dist\Mission4Me-Setup-$Version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Mission4Me-Setup-$Version.exe" | Set-Content -Encoding ascii "$setup.sha256"

$mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "`nPronto: $setup ($mb MB)" -ForegroundColor Green
Write-Host "SHA-256: $hash"
