[CmdletBinding()]
param(
    [string]$Version = "0.1.8-alpha.1"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $projectRoot "artifacts"
$publishDirectory = Join-Path $projectRoot "build\publish-full"
$fullProject = Join-Path $projectRoot "src\UmaSeedInstaller.App\UmaSeedInstaller.App.csproj"
$compactProject = Join-Path $projectRoot "src\UmaSeedInstaller.Compact\UmaSeedInstaller.Compact.csproj"
$compactProjectDirectory = Split-Path -Parent $compactProject
$fullTestProject = Join-Path $projectRoot "tests\UmaSeedInstaller.Tests\UmaSeedInstaller.Tests.csproj"
$compactTestProject = Join-Path $projectRoot "tests\UmaSeedInstaller.Compact.Tests\UmaSeedInstaller.Compact.Tests.csproj"

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

dotnet run --project $fullTestProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Full 版自动测试失败。"
}

dotnet run --project $compactTestProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Compact 版自动测试失败。"
}

dotnet publish $fullProject -c Release -r win-x64 --self-contained true -o $publishDirectory `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Full 版发布构建失败。"
}

dotnet build $compactProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Compact 版发布构建失败。"
}

$assets = @(
    @{
        Source = Join-Path $compactProjectDirectory "bin\Release\net48\种马搜索器安装助手-Compact.exe"
        Name = "UmaSeedInstaller-v$Version-win-compact.exe"
    },
    @{
        Source = Join-Path $publishDirectory "种马搜索器安装助手.exe"
        Name = "UmaSeedInstaller-v$Version-win-x64-full.exe"
    }
)

foreach ($asset in $assets) {
    $assetPath = Join-Path $artifactDirectory $asset.Name
    Remove-Item -LiteralPath $assetPath, "$assetPath.sha256.txt" -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath ([System.IO.Path]::GetFullPath($asset.Source)) -Destination $assetPath
    $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$assetPath.sha256.txt" -Value "$hash  $($asset.Name)" -Encoding utf8NoBOM
    $size = (Get-Item -LiteralPath $assetPath).Length
    Write-Host "构建完成：$assetPath ($size bytes)"
    Write-Host "SHA256：$hash"
}
