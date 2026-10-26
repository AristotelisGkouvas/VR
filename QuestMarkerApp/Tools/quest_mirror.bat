@echo off
rem Mirror the Quest 3 view on the PC (scrcpy). Double-click, or run with:
rem   quest_mirror.bat          both eyes, over USB
rem   quest_mirror.bat eye      one eye only (bigger picture)
rem   quest_mirror.bat wifi     over Wi-Fi (connect USB once first)
rem   quest_mirror.bat rec      also record to quest_record.mp4 next to this file (overwrites)

setlocal
rem Use the same adb as Unity so the two don't fight over the adb server.
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if not exist "%ADB%" set "ADB="
set "QUEST_IP=192.168.1.30"

where scrcpy >nul 2>nul
if errorlevel 1 (
  echo scrcpy not found. Install it with:  winget install Genymobile.scrcpy
  pause
  exit /b 1
)

rem --no-control: clicks on the PC window must not reach the headset.
rem --render-driver=opengl: the default direct3d11 renderer shows only a grey window on this PC.
set "ARGS=--no-control --no-audio --render-driver=opengl --max-size 1600 --video-bit-rate 16M --max-fps 60 --window-title Quest3"

if /i "%1"=="eye" set "ARGS=%ARGS% --crop 2064:2208:0:0"
if /i "%1"=="rec" set ARGS=%ARGS% --record "%~dp0quest_record.mp4"

if /i "%1"=="wifi" (
  if defined ADB (
    "%ADB%" tcpip 5555
    "%ADB%" connect %QUEST_IP%:5555
  ) else (
    adb tcpip 5555
    adb connect %QUEST_IP%:5555
  )
  set "ARGS=%ARGS% --tcpip=%QUEST_IP%:5555"
) else (
  set "ARGS=%ARGS% --select-usb"
)

scrcpy %ARGS%
if errorlevel 1 (
  echo.
  echo Failed. Check: headset on, USB cable connected, "Allow USB debugging" accepted inside the headset.
  pause
)
