param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'

dotnet restore 'tests\StalkerTrainer.Tests\StalkerTrainer.Tests.csproj' --configfile 'NuGet.Config'
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
if (-not $SkipTests) {
    dotnet run --project 'tests\StalkerTrainer.Tests\StalkerTrainer.Tests.csproj' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
dotnet publish 'src\StalkerTrainer\StalkerTrainer.csproj' -c Release --no-restore --self-contained false -o 'dist\StalkerTrainer'
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath 'README.ru.md' -Destination 'dist\StalkerTrainer\README.ru.md' -Force
Copy-Item -LiteralPath 'VALIDATION.md' -Destination 'dist\StalkerTrainer\VALIDATION.md' -Force
Compress-Archive -Path 'dist\StalkerTrainer' -DestinationPath 'dist\StalkerTrainer-CSharp.zip' -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskSourceZipPath = Join-Path $PSScriptRoot 'dist\StalkerTrainer-CSharp-Source.zip'
$taskSourceStream = [IO.File]::Open($taskSourceZipPath, [IO.FileMode]::Create)
$taskSourceZip = New-Object IO.Compression.ZipArchive($taskSourceStream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    $taskSourceFiles = @('README.ru.md', 'VALIDATION.md', 'build.ps1', 'NuGet.Config', 'global.json', '.gitignore') | ForEach-Object { Get-Item -LiteralPath $_ }
    $taskSourceFiles += Get-ChildItem -LiteralPath 'src', 'tests' -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    foreach ($taskSourceFile in $taskSourceFiles) {
        $taskEntryName = $taskSourceFile.FullName.Substring($PSScriptRoot.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskSourceZip, $taskSourceFile.FullName, $taskEntryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $taskSourceZip.Dispose(); $taskSourceStream.Dispose() }
Write-Output 'Ready: dist\StalkerTrainer\StalkerTrainer.exe'
