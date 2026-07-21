# SETT Memory Cleaner - Build Script
Write-Host "[BUILD] SETT Memory Cleaner" -ForegroundColor Cyan
Write-Host ""

# ============================================================================
# VALIDAR DEPENDENCIAS
# ============================================================================

# 1. Verificar MSBuild
Write-Host "[1/3] Verificando MSBuild..." -ForegroundColor Yellow
$msbuild = Get-ChildItem "C:\Program Files*\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName

if (-not $msbuild) {
    Write-Host "[ERRO] MSBuild nao encontrado!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Solucao:" -ForegroundColor Yellow
    Write-Host "  winget install Microsoft.VisualStudio.2022.BuildTools" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Ou download manual:" -ForegroundColor Yellow
    Write-Host "  https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022" -ForegroundColor Cyan
    exit 1
}
Write-Host "  OK: $msbuild" -ForegroundColor Green

# 2. Verificar .NET Framework 4.0
Write-Host "[2/3] Verificando .NET Framework 4.0..." -ForegroundColor Yellow
$dotnet40 = Test-Path "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0"
if (-not $dotnet40) {
    Write-Host "  AVISO: .NET Framework 4.0 Target Pack nao encontrado" -ForegroundColor Yellow
    Write-Host "  Build pode falhar se nao tiver instalado" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Solucao:" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet-framework/net40" -ForegroundColor Cyan
} else {
    Write-Host "  OK: .NET Framework 4.0 Target Pack instalado" -ForegroundColor Green
}

# 3. Verificar NuGet (opcional - MSBuild faz restore automatico)
Write-Host "[3/3] Verificando NuGet..." -ForegroundColor Yellow
$nuget = Get-Command nuget -ErrorAction SilentlyContinue
if (-not $nuget) {
    Write-Host "  INFO: NuGet CLI nao encontrado (opcional - MSBuild faz restore)" -ForegroundColor Gray
} else {
    Write-Host "  OK: NuGet CLI instalado" -ForegroundColor Green
}

Write-Host ""

# ============================================================================
# BUILD
# ============================================================================

Write-Host "=== INICIANDO BUILD ===" -ForegroundColor Cyan
Write-Host ""

# Restore + Build
Write-Host "[1/2] Restaurando pacotes NuGet..." -ForegroundColor Yellow
& $msbuild "src\SETTMemoryCleaner.sln" /t:Restore /p:Configuration=Release /v:minimal /nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "[ERRO] Falha ao restaurar pacotes!" -ForegroundColor Red
    exit 1
}

Write-Host "[2/2] Compilando Release..." -ForegroundColor Yellow
& $msbuild "src\SETTMemoryCleaner.sln" /p:Configuration=Release /p:Platform="Any CPU" /v:minimal /nologo

# ============================================================================
# RESULTADO
# ============================================================================

Write-Host ""
if ($LASTEXITCODE -eq 0) {
    Write-Host "================================================================" -ForegroundColor Green
    Write-Host "  BUILD CONCLUIDO COM SUCESSO" -ForegroundColor Green
    Write-Host "================================================================" -ForegroundColor Green
    Write-Host ""

    $exe = Get-Item "src\bin\Release\SETTMemoryCleaner.exe" -ErrorAction SilentlyContinue
    if ($exe) {
        $sizeKB = [Math]::Round($exe.Length / 1KB, 2)
        Write-Host "  Executavel:  src\bin\Release\SETTMemoryCleaner.exe" -ForegroundColor Cyan
        Write-Host "  Tamanho:     $sizeKB KB" -ForegroundColor Cyan
        Write-Host "  Modificado:  $($exe.LastWriteTime.ToString('dd/MM/yyyy HH:mm:ss'))" -ForegroundColor Cyan
    } else {
        Write-Host "  Executavel:  src\bin\Release\SETTMemoryCleaner.exe" -ForegroundColor Cyan
    }

    Write-Host ""
    Write-Host "Proximos passos:" -ForegroundColor Yellow
    Write-Host "  1. Executar:  .\src\bin\Release\SETTMemoryCleaner.exe" -ForegroundColor Gray
    Write-Host "  2. Release:   git tag v1.0.0; git push origin v1.0.0" -ForegroundColor Gray
    Write-Host ""
} else {
    Write-Host "================================================================" -ForegroundColor Red
    Write-Host "  BUILD FALHOU" -ForegroundColor Red
    Write-Host "================================================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "Verificar:" -ForegroundColor Yellow
    Write-Host "  1. Erros de compilacao acima" -ForegroundColor Gray
    Write-Host "  2. .NET Framework 4.0 instalado" -ForegroundColor Gray
    Write-Host "  3. Arquivos src/*.cs sem erros de sintaxe" -ForegroundColor Gray
    Write-Host ""
    exit 1
}
