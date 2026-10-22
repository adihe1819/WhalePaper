@echo off
setlocal
set "WHALEPAPER_DATA=%~dp0data"
if not exist "%WHALEPAPER_DATA%" mkdir "%WHALEPAPER_DATA%"
start "" "%~dp0WhalePaper.exe" %*
endlocal
