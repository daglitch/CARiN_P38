@echo off
rem Builds the viewer data from the disc image (first run only), then serves the viewer.
setlocal
cd /d "%~dp0"
set WORK=%TEMP%\p38_carin
set BIN=P38_Navigation_CD_2002_California_Nevada.bin

if not exist "viewer\data\level1.json" (
  echo Extracting disc image to %WORK% ...
  py tools\carin.py extract "%BIN%" "%WORK%" || goto :err
  py tools\carin.py export "%WORK%\carindb" viewer\data || goto :err
)

if not exist "viewer\data\l0\index.json" (
  rem Street level is compressed; decoding uses OpenCarin (MIT licence).
  if not exist "third_party\OpenCarin" (
    git clone --depth 1 https://github.com/fdemusso/OpenCarin.git third_party\OpenCarin || goto :err
  )
  py tools\carin.py iso "%BIN%" "%WORK%\disc.iso" || goto :err
  echo Decoding street tiles, this takes about 15 minutes ...
  py tools\carin.py streets "%WORK%\disc.iso" third_party\OpenCarin viewer\data || goto :err
)

start "" http://127.0.0.1:8738/
py -m http.server 8738 --bind 127.0.0.1 --directory viewer
goto :eof
:err
echo Failed. Check that Python (the "py" launcher) and git are installed.
pause
