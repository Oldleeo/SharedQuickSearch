param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Offline,
    [string]$UpdateUrl = '',
    [string]$SigningKey = '',
    [switch]$AllowUnsignedUpdates
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$appProject = Join-Path $projectRoot 'src/SharedQuickSearch/SharedQuickSearch.csproj'
$testProject = Join-Path $projectRoot 'tests/SharedQuickSearch.Tests/SharedQuickSearch.Tests.csproj'
$coreTestProject = Join-Path $projectRoot 'tests/SharedQuickSearch.CoreTests/SharedQuickSearch.CoreTests.csproj'
$signer = Join-Path $projectRoot 'tools/UpdateSigner/UpdateSigner.csproj'
$publishFolder = Join-Path $projectRoot 'publish'
$installerFolder = Join-Path $projectRoot 'installer'
if (-not (Select-String -LiteralPath (Join-Path $projectRoot 'src/SharedQuickSearch/UpdateKey.cs') -Pattern 'BEGIN PUBLIC KEY' -Quiet)) {
    if (-not $AllowUnsignedUpdates) { throw '请先用 tools/UpdateSigner keygen 配置更新公钥。' }
    Write-Warning '仅用于本机试编译：此版本不能接收自动更新，不要分发。'
}
$restoreOptions = @()
if ($Offline) { $restoreOptions = @('--configfile', (Join-Path $projectRoot 'NuGet.offline.Config')) }
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code ${LASTEXITCODE}: $($args -join ' ')" }
}
Invoke-Dotnet restore $testProject @restoreOptions
Invoke-Dotnet restore $coreTestProject @restoreOptions
Invoke-Dotnet restore $signer @restoreOptions
Invoke-Dotnet build $appProject -c $Configuration --no-restore
Invoke-Dotnet run --project $testProject -c $Configuration --no-restore
Invoke-Dotnet run --project $coreTestProject -c $Configuration --no-restore
Invoke-Dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true --no-restore -o $publishFolder
[xml]$project = Get-Content -LiteralPath $appProject -Encoding UTF8
$version = $project.Project.PropertyGroup.Version | Select-Object -First 1
New-Item -ItemType Directory -Force -Path $installerFolder | Out-Null
$exe = Join-Path $publishFolder '共享速搜.exe'
Copy-Item -LiteralPath $exe -Destination (Join-Path $installerFolder "共享速搜-$version-安装.exe") -Force
Copy-Item -LiteralPath $exe -Destination (Join-Path $installerFolder 'SharedQuickSearch-Setup.exe') -Force
Copy-Item -LiteralPath $exe -Destination (Join-Path $installerFolder 'SharedQuickSearch-Portable.exe') -Force
Get-FileHash -LiteralPath (Join-Path $installerFolder 'SharedQuickSearch-Setup.exe'), (Join-Path $installerFolder 'SharedQuickSearch-Portable.exe') -Algorithm SHA256 |
    ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path -Leaf $_.Path) } |
    Set-Content -LiteralPath (Join-Path $installerFolder 'SHA256SUMS.txt') -Encoding ascii
if ($UpdateUrl) {
    $manifest = Join-Path $installerFolder 'update.json'
    Invoke-Dotnet run --project $signer -c $Configuration --no-restore -- manifest --exe (Join-Path $installerFolder 'SharedQuickSearch-Setup.exe') --version $version --url $UpdateUrl --out $manifest
    $keyOptions = @()
    if ($SigningKey) { $keyOptions = @('--key', $SigningKey) }
    Invoke-Dotnet run --project $signer -c $Configuration --no-restore -- sign $manifest @keyOptions
    Invoke-Dotnet run --project $signer -c $Configuration --no-restore -- verify --manifest $manifest --exe (Join-Path $installerFolder 'SharedQuickSearch-Setup.exe')
}
Write-Host "程序：$exe"
Write-Host "安装包：$installerFolder/SharedQuickSearch-Setup.exe"
Write-Host "便携版：$installerFolder/SharedQuickSearch-Portable.exe"
