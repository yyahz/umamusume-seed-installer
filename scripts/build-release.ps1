[CmdletBinding()]
param(
    [string]$Version = "0.1.1-alpha.1"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $projectRoot "artifacts"
$publishDirectory = Join-Path $projectRoot "build\publish"
$projectPath = Join-Path $projectRoot "src\UmaSeedInstaller.App\UmaSeedInstaller.App.csproj"
$testProject = Join-Path $projectRoot "tests\UmaSeedInstaller.Tests\UmaSeedInstaller.Tests.csproj"

if (Test-Path -LiteralPath $artifactDirectory) {
    Remove-Item -LiteralPath $artifactDirectory -Recurse -Force
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactDirectory | Out-Null

dotnet run --project $testProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "自动测试失败。"
}

dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $publishDirectory `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "发布构建失败。"
}

$sourceExecutable = Join-Path $publishDirectory "种马搜索器安装助手.exe"
$assetName = "UmaSeedInstaller-v$Version-win-x64.exe"
$assetPath = Join-Path $artifactDirectory $assetName
Copy-Item -LiteralPath $sourceExecutable -Destination $assetPath

$hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashLine = "$hash  $assetName"
Set-Content -LiteralPath "$assetPath.sha256.txt" -Value $hashLine -Encoding utf8NoBOM

Write-Host "构建完成：$assetPath"
Write-Host "SHA256：$hash"
