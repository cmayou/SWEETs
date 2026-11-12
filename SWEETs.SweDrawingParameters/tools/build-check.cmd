@echo off
rem Compiles the add-in and the DWG checks and writes tools\build-check-result.txt. Installs nothing.
setlocal
set OUT=%~dp0build-check-result.txt
echo SWEETs build check %DATE% %TIME% > "%OUT%"
cd /d "%~dp0.."
echo ===== add-in ===== >> "%OUT%"
dotnet build "src\SWEETs.SweDrawingParameters.AddIn\SWEETs.SweDrawingParameters.AddIn.csproj" -c Release -p:Platform=x64 --nologo -v q >> "%OUT%" 2>&1
echo ===== dwg checks ===== >> "%OUT%"
dotnet build "tools\SWEETs.DwgPatch.Tests\SWEETs.DwgPatch.Tests.csproj" -c Release --nologo -v q >> "%OUT%" 2>&1
"tools\SWEETs.DwgPatch.Tests\bin\Release\SWEETs.DwgPatch.Tests.exe" >> "%OUT%" 2>&1
echo ===== finished ===== >> "%OUT%"
endlocal
