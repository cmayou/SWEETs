@echo off
rem Compiles the add-in and the format checks, runs the checks, and writes tools\build-check-result.txt. Installs nothing.
setlocal
set OUT=%~dp0build-check-result.txt
set SUPPORT=%ProgramW6432%\SOLIDWORKS Corp\SOLIDWORKS Electrical\Support
echo SWEETs Line Style Editor build check %DATE% %TIME% > "%OUT%"
cd /d "%~dp0.."
echo ===== add-in ===== >> "%OUT%"
dotnet build "src\SWEETs.SweLineStyles.AddIn\SWEETs.SweLineStyles.AddIn.csproj" -c Release -p:Platform=x64 --nologo -v q >> "%OUT%" 2>&1
echo ===== format checks ===== >> "%OUT%"
dotnet build "tools\SWEETs.LineStyles.Tests\SWEETs.LineStyles.Tests.csproj" -c Release --nologo -v q >> "%OUT%" 2>&1
"tools\SWEETs.LineStyles.Tests\bin\Release\SWEETs.LineStyles.Tests.exe" "%SUPPORT%" >> "%OUT%" 2>&1
echo ===== finished ===== >> "%OUT%"
endlocal
