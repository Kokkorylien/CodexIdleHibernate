param([string]$OutputDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Push-Location -LiteralPath $PSScriptRoot
try {
    & .\make-icon.ps1
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /win32icon:app.ico "/out:$OutputDirectory\CodexIdleHibernate.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll MonitorCore.cs App.cs DesktopIdentity.cs AssemblyInfo.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (Test-Path -LiteralPath 'Tests.cs') {
        & $compiler /nologo /target:exe /platform:x64 "/out:$OutputDirectory\Tests.exe" /reference:System.Web.Extensions.dll MonitorCore.cs Tests.cs
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
        & (Join-Path $OutputDirectory 'Tests.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
} finally { Pop-Location }
