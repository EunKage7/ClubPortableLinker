<#
  smoke.ps1 — быстрый дымовой тест CLI-конвейера БЕЗ прав администратора и
  без изменения реальной системы. Прогоняет на синтетической папке:
    --pack  ->  --verify-package  ->  --plan --json  ->  --export-zip
  и проверяет коды возврата и артефакты. Падает с ненулевым кодом при любой
  проблеме — удобно для CI и проверки после правок движка.

  Важно: бинарник — WinExe (GUI-подсистема), поэтому запускаем строго через
  Start-Process -Wait -PassThru с перенаправлением вывода в файлы. Иначе
  PowerShell не дожидается завершения и не получает код возврата.

  Запуск:
    powershell -NoProfile -File tools\smoke.ps1
    powershell -NoProfile -File tools\smoke.ps1 -Exe "путь\к\ClubPortableLinker.exe"
#>
param(
    [string]$Exe = "$PSScriptRoot\..\bin\Release\net8.0-windows\ClubPortableLinker.exe"
)

$ErrorActionPreference = "Stop"
$failed = 0

function Invoke-Cli([string[]]$cliArgs) {
    $o = [IO.Path]::GetTempFileName()
    $e = [IO.Path]::GetTempFileName()
    try {
        $p = Start-Process -FilePath $script:Exe -ArgumentList $cliArgs `
            -NoNewWindow -Wait -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
        $out = Get-Content -Raw -LiteralPath $o -ErrorAction SilentlyContinue
        $err = Get-Content -Raw -LiteralPath $e -ErrorAction SilentlyContinue
        return [pscustomobject]@{ Code = $p.ExitCode; Text = "$out`n$err" }
    }
    finally {
        Remove-Item -LiteralPath $o, $e -Force -ErrorAction SilentlyContinue
    }
}

function Step($name, [scriptblock]$body) {
    Write-Host "== $name ==" -ForegroundColor Cyan
    try { & $body; Write-Host "   OK" -ForegroundColor Green }
    catch { $script:failed++; Write-Host "   FAIL: $($_.Exception.Message)" -ForegroundColor Red }
}

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host "Не найден exe: $Exe. Сначала: dotnet build -c Release" -ForegroundColor Red
    exit 2
}
$Exe = (Resolve-Path -LiteralPath $Exe).Path

$work = Join-Path ([IO.Path]::GetTempPath()) ("cpl-smoke-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$src = Join-Path $work "SourceApp"
$dst = Join-Path $work "Portable\FooApp"
$zip = Join-Path $work "FooApp-portable.zip"
New-Item -ItemType Directory -Force -Path (Join-Path $src "Data\FooApp") | Out-Null

# Синтетический «установленный» пакет: launcher + bat со ссылкой + reg + данные.
Set-Content -LiteralPath (Join-Path $src "app.exe") -Value "MZ-stub" -Encoding ASCII
Set-Content -LiteralPath (Join-Path $src "Data\FooApp\config.dat") -Value "data" -Encoding ASCII
@'
@echo off
mklink /J "C:\ProgramData\FooAppSmoke" "%~dp0Data\FooApp"
mklink /D C:\FooSmokeDirLink %~dp0Data\FooApp
mklink /H "C:\FooSmokeFile.dat" "%~dp0Data\FooApp\config.dat"
start "" "%~dp0app.exe"
'@ | Set-Content -LiteralPath (Join-Path $src "Run.bat") -Encoding ASCII
@'
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\FooAppSmoke]
"Installed"="1"
'@ | Set-Content -LiteralPath (Join-Path $src "install.reg") -Encoding ASCII

try {
    Step "pack" {
        $r = Invoke-Cli @("--pack", "--source", $src, "--destination", $dst, "--name", "FooApp")
        if ($r.Code -ne 0) { throw "exit $($r.Code): $($r.Text)" }
        if (-not (Test-Path -LiteralPath (Join-Path $dst ".portable\manifest.json"))) { throw "нет manifest.json" }
        if (-not (Test-Path -LiteralPath (Join-Path $dst "Run.cmd"))) { throw "нет Run.cmd" }
    }

    Step "mklink Kind by flag J/D/H" {
        $maniPath = Join-Path $dst ".portable\manifest.json"
        $mani = Get-Content -Raw -LiteralPath $maniPath | ConvertFrom-Json
        $kinds = @($mani.Profiles[0].Links | ForEach-Object { "$($_.Kind)" } | Sort-Object)
        $got = $kinds -join ","
        # /J -> Junction, /D -> SymlinkDir, /H -> HardlinkFile (sorted alpha)
        if ($got -ne "HardlinkFile,Junction,SymlinkDir") {
            throw ("Link Kinds = " + $got + ", expected HardlinkFile,Junction,SymlinkDir")
        }
    }

    Step "verify-package (json валиден)" {
        $r = Invoke-Cli @("--verify-package", "--package", $dst, "--json")
        # verify может вернуть 0 (ок) или 2 (есть ошибки) — проверяем валидность JSON.
        $null = $r.Text.Trim() | ConvertFrom-Json
    }

    Step "plan --json (3 ссылки: /J /D /H)" {
        $r = Invoke-Cli @("--plan", "--json", "--config", $dst, "--profile", "FooApp")
        if ($r.Code -ne 0) { throw "exit $($r.Code)" }
        $plan = $r.Text.Trim() | ConvertFrom-Json
        if ($plan.ProfileName -ne "FooApp") { throw "ProfileName=$($plan.ProfileName)" }
        # Парсер mklink должен поймать все три варианта (/J в кавычках, /D без кавычек, /H).
        if ($plan.LinkCount -ne 3) { throw "ожидалось 3 ссылки, получено LinkCount=$($plan.LinkCount)" }
    }

    Step "export-zip (--force, без _Replaced)" {
        $r = Invoke-Cli @("--export-zip", "--package", $dst, "--out", $zip, "--force")
        if (-not (Test-Path -LiteralPath $zip)) { throw "zip не создан: $($r.Text)" }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            if ($archive.Entries.Count -eq 0) { throw "zip пуст" }
            if ($archive.Entries | Where-Object { $_.FullName -match "(^|/)_Replaced/" }) { throw "в zip попал _Replaced" }
            if ($archive.Entries | Where-Object { $_.FullName -match "(^|/)_Backups/" }) { throw "в zip попал _Backups" }
        }
        finally { $archive.Dispose() }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "`nПРОВАЛЕНО шагов: $failed" -ForegroundColor Red; exit 1 }
Write-Host "`nВсе дымовые тесты прошли." -ForegroundColor Green
exit 0
