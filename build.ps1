param([string]$GuiName = 'LoL语言工具.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '未找到 Windows .NET Framework C# 编译器。' }
$outputDirectory = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$sourcePaths = @((Join-Path $projectRoot 'src\LocaleEngine.cs'), (Join-Path $projectRoot 'src\Program.cs'))
$guiPath = Join-Path $outputDirectory $GuiName
& $compilerPath /nologo /optimize+ /target:winexe /platform:anycpu "/out:$guiPath" "/win32manifest:$(Join-Path $projectRoot 'src\app.manifest')" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @sourcePaths
if ($LASTEXITCODE -ne 0) { throw '图形界面编译失败。' }
$cliPath = Join-Path $outputDirectory 'LoL语言工具-cli.exe'
& $compilerPath /nologo /optimize+ /target:exe /platform:anycpu "/out:$cliPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @sourcePaths
if ($LASTEXITCODE -ne 0) { throw '命令行工具编译失败。' }
Write-Output "编译完成：$guiPath"
