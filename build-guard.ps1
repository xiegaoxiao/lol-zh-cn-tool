param([string]$OutputName = 'LoL单文件保护.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '未找到 Windows .NET Framework C# 编译器。' }
$outputDirectory = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$outputPath = Join-Path $projectRoot ('dist\' + $OutputName)
& $compilerPath /nologo /optimize+ /target:winexe /platform:anycpu /main:LolLocaleTool.GuardProgram "/out:$outputPath" "/win32manifest:$(Join-Path $projectRoot 'src\app.manifest')" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $projectRoot 'src\LocaleEngine.cs') (Join-Path $projectRoot 'src\DefensiveProtection.cs') (Join-Path $projectRoot 'src\ProductGuard.cs')
if ($LASTEXITCODE -ne 0) { throw '单文件保护工具编译失败。' }
Write-Output "编译完成：$outputPath"
