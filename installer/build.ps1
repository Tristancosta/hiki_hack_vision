# Gera o instalador do HikiHackVision em dist\HikiHackVision-Setup-<versão>.exe.
# Uso: powershell -ExecutionPolicy Bypass -File installer\build.ps1
# Requer o Inno Setup 6 (winget install JRSoftware.InnoSetup). A versão vem do <Version> do .csproj.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\HikiHackVision\HikiHackVision.csproj'
$publishDir = Join-Path $root 'publish\win-x64'
$toolsDir = Join-Path $root 'tools'
$version = (Select-Xml -Path $project -XPath '//Version').Node.InnerText

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup' }

# 1) Publica o app autossuficiente num único .exe: o outro computador não precisa ter o .NET instalado
Write-Host "Publicando HikiHackVision $version (win-x64)..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=None -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Falha no dotnet publish.' }

# 2) Empacota com o FFmpeg de tools\ (se existir), para funcionar sem internet
if (-not ((Test-Path "$toolsDir\ffmpeg.exe") -and (Test-Path "$toolsDir\ffprobe.exe"))) {
    Write-Warning "FFmpeg não encontrado em $toolsDir; o instalador sairá sem ele e o app vai baixá-lo na primeira execução."
}
Write-Host 'Compilando o instalador (a compressão do FFmpeg leva alguns minutos)...'
& $iscc /Q "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DToolsDir=$toolsDir" (Join-Path $PSScriptRoot 'HikiHackVision.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador.' }

$setup = Join-Path $root "dist\HikiHackVision-Setup-$version.exe"
Write-Host ("Instalador gerado: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
