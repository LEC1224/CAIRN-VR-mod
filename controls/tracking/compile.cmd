@echo off
setlocal
call "%~1" >nul
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /MT /EHsc /LD /I"%~2" "%~dp0Tracking.cpp" /Fo"%~3\Tracking.obj" /link /OUT:"%~3\CairnTracking.dll" /IMPLIB:"%~3\Tracking.lib"
exit /b %errorlevel%
