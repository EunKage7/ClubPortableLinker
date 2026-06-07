param(
    [string]$Output = "$PSScriptRoot\publish",
    [switch]$Zip
)

$ErrorActionPreference = "Stop"

dotnet publish "$PSScriptRoot\ClubPortableLinker.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $Output

$pdb = Join-Path $Output "ClubPortableLinker.pdb"
if (Test-Path -LiteralPath $pdb) {
    Remove-Item -LiteralPath $pdb -Force
}

if ($Zip) {
    $zipPath = Join-Path (Split-Path -Parent $Output) "ClubPortableLinker-win-x64.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $zipPath -Force
    Write-Host "ZIP: $zipPath"
}

Write-Host "Publish: $Output"

