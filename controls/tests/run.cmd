@echo off
setlocal
call "%~1" >nul
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /MT /I"%~2" "%~dp0TrackingTest.cpp" /Fo"%~3\TrackingTest.obj" /link /OUT:"%~3\TrackingTest.exe"
if errorlevel 1 exit /b 1
"%~3\TrackingTest.exe" "%~3\CairnTracking.dll"
exit /b %errorlevel%
