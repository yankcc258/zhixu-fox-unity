@echo off
rem Kiko voice service: Unity -> this bridge (127.0.0.1:9876) -> GPT-SoVITS inference WebUI (127.0.0.1:9872)
rem Start the GPT-SoVITS WebUI first: go-webui.bat, tab 1C, "open TTS inference WebUI".
cd /d "%~dp0"
set "GSV=D:\GPT-SoVITS-v4-20250422fix\GPT-SoVITS-v4-20250422fix"
if not exist "%GSV%\runtime\python.exe" (
  echo GPT-SoVITS not found at %GSV% - edit this file.
  pause
  exit /b 1
)
"%GSV%\runtime\python.exe" voice_bridge.py
pause
