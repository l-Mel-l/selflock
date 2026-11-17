# Локальный эквивалент GitHub Actions workflow .github/workflows/ci.yml.
# Запуск из корня репозитория:
#   powershell -ExecutionPolicy Bypass -File tools\ci.ps1
# Для быстрого прогона без публикации:
#   powershell -ExecutionPolicy Bypass -File tools\ci.ps1 -SkipPublish

param(
    [string]$Configuration = "Release",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding($false)
try {
    [Console]::OutputEncoding = $utf8
    $OutputEncoding = $utf8
} catch { }

$root = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $root "artifacts"
$log = Join-Path $logDir ("ci-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
[IO.File]::WriteAllText($log, "", $utf8)

function Write-Log {
    param([string]$Text)
    Write-Host $Text
    [IO.File]::AppendAllText($script:log, $Text + "`r`n", $script:utf8)
}

function Invoke-DotNet {
    param([string]$Title, [string[]]$Arguments)
    Write-Log ""
    Write-Log "==> $Title"
    Write-Log "    dotnet $($Arguments -join ' ')"
    $output = & dotnet @Arguments 2>&1 | Out-String
    if ($output.Length -gt 0) {
        Write-Host $output -NoNewline
        [IO.File]::AppendAllText($script:log, $output, $script:utf8)
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Log "!!! Шаг завершился с кодом $LASTEXITCODE"
        throw "Шаг «$Title» завершился с кодом $LASTEXITCODE."
    }
}

Push-Location $root
try {
    Write-Log "SelfLock CI, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    Write-Log "Каталог: $root"
    Write-Log "Конфигурация: $Configuration"

    Invoke-DotNet "Восстановление зависимостей" @("restore", "SelfLock.sln")
    Invoke-DotNet "Проверка форматирования" @("format", "SelfLock.sln", "--verify-no-changes", "--no-restore")
    Invoke-DotNet "Сборка Release" @("build", "SelfLock.sln", "-c", $Configuration, "--no-restore")
    Invoke-DotNet "Модульные тесты" @(
        "test", "SelfLock.sln", "-c", $Configuration, "--no-build",
        "--logger", "trx;LogFileName=test-results.trx",
        "--results-directory", "TestResults"
    )

    if (-not $SkipPublish) {
        Invoke-DotNet "Публикация SelfLock.exe" @(
            "publish", "src/SelfLock.App/SelfLock.App.csproj", "-c", $Configuration,
            "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true",
            "-o", "publish"
        )
    }

    Write-Log ""
    Write-Log "CI завершён успешно."
}
catch {
    Write-Log ""
    Write-Log "CI завершён с ошибкой: $($_.Exception.Message)"
    throw
}
finally {
    Pop-Location
    Write-Host "Журнал: $log"
}
