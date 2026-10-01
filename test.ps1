$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testOutput = Join-Path $projectRoot 'tests\EngineTests.exe'
& $compilerPath /nologo /target:exe "/out:$testOutput" (Join-Path $projectRoot 'src\LocaleEngine.cs') (Join-Path $projectRoot 'src\DefensiveProtection.cs') (Join-Path $projectRoot 'tests\EngineTests.cs')
if ($LASTEXITCODE -ne 0) { throw '测试程序编译失败。' }
& $testOutput (Join-Path $projectRoot 'tests')
if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
