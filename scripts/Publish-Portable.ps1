param(
    [switch]$Offline,
    [switch]$KeepPublishDirectory,
    [string]$OutputDirectory = 'artifacts/publish'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be inside this project''s artifacts directory.'
}
$project = Join-Path $projectRoot 'src/LiteHibernate/LiteHibernate.csproj'
function Complete-Package {
    $archive = Join-Path $projectRoot 'artifacts/LiteHibernate-win-x64.zip'
    # Archive the contents, so extraction never introduces a publish directory.
    $packageFiles = @(Get-ChildItem -LiteralPath $outputPath | Select-Object -ExpandProperty FullName)
    Compress-Archive -LiteralPath $packageFiles -DestinationPath $archive -CompressionLevel Optimal -Force
    if (-not $KeepPublishDirectory) {
        $resolvedOutput = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $outputPath).Path)
        if (-not $resolvedOutput.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe publish cleanup path.' }
        if ((Get-Item -LiteralPath $resolvedOutput).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to remove a directory link.' }
        Remove-Item -LiteralPath $resolvedOutput -Recurse
    }
    Write-Output "Portable archive: $archive"
}
if (-not $Offline) {
    & dotnet publish $project -c Release -r win-x64 --self-contained true '-p:DebugType=None' -o $outputPath --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. If runtime packages cannot be downloaded, use -Offline.' }
    Complete-Package
    exit 0
}

# The SDK supports an app-relative .NET installation. Bundle installed Microsoft runtimes
# unchanged, rather than reconstructing NuGet runtime packages or editing dependency manifests.
$dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
$coreRoot = Join-Path $dotnetRoot 'shared/Microsoft.NETCore.App'
$desktopRoot = Join-Path $dotnetRoot 'shared/Microsoft.WindowsDesktop.App'
$version = Get-ChildItem -LiteralPath $coreRoot -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' -and (Test-Path -LiteralPath (Join-Path $desktopRoot $_.Name)) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
if (-not $version) { throw 'A matching .NET 10 x64 Core and Windows Desktop runtime is required for offline packaging.' }
$hostDirectory = Join-Path $dotnetRoot "host/fxr/$version"
if (-not (Test-Path -LiteralPath $hostDirectory)) { throw 'Matching hostfxr runtime was not found.' }
& dotnet publish $project -c Release --self-contained false '-p:DebugType=None' '-p:AppHostDotNetSearch=AppRelative' '-p:AppHostRelativeDotNet=runtime' -o $outputPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Offline publish failed.' }
$runtimePath = Join-Path $outputPath 'runtime'
foreach ($directory in @('host/fxr', 'shared/Microsoft.NETCore.App', 'shared/Microsoft.WindowsDesktop.App')) {
    New-Item -ItemType Directory -Path (Join-Path $runtimePath $directory) -Force | Out-Null
}
Copy-Item -LiteralPath $hostDirectory -Destination (Join-Path $runtimePath 'host/fxr') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $coreRoot $version) -Destination (Join-Path $runtimePath 'shared/Microsoft.NETCore.App') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $desktopRoot $version) -Destination (Join-Path $runtimePath 'shared/Microsoft.WindowsDesktop.App') -Recurse -Force
foreach ($name in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $source = Join-Path $dotnetRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $runtimePath -Force }
}
Write-Output "Portable app with bundled local .NET ${version}: $outputPath"
Complete-Package
