param([string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a version such as 1.0.0.' }
$packageRoot = Join-Path $PSScriptRoot "dist\CodexIdleHibernate-v$Version-win-x64"
$compileRoot = Join-Path $PSScriptRoot "build\v$Version"
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $compileRoot
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
foreach ($name in @('README.md', 'context.example.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $packageRoot $name) -Force
}
Copy-Item -LiteralPath (Join-Path $compileRoot 'CodexIdleHibernate.exe') -Destination (Join-Path $packageRoot 'CodexIdleHibernate.exe') -Force
$archive = "$packageRoot.zip"
$packageFiles = @('CodexIdleHibernate.exe', 'README.md', 'context.example.json') | ForEach-Object { Join-Path $packageRoot $_ }
Compress-Archive -LiteralPath $packageFiles -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
Write-Output $archive
