param([string]$GptSovitsRoot = 'D:\GPT-SoVITS-v4-20250422fix\GPT-SoVITS-v4-20250422fix')
$ErrorActionPreference = 'Stop'
$bundledPython = Join-Path $GptSovitsRoot 'runtime/python.exe'
if (-not (Test-Path -LiteralPath $bundledPython)) { throw 'Pass -GptSovitsRoot with your GPT-SoVITS installation directory.' }
& $bundledPython (Join-Path $PSScriptRoot 'voice_bridge.py')
