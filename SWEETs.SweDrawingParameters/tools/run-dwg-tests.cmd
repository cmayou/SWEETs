@echo off
rem Builds and runs the DWG patcher checks. Writes tools\dwg-tests-result.txt. Changes no drawing.
setlocal
set OUT=%~dp0dwg-tests-result.txt
echo SWEETs DWG patcher checks %DATE% %TIME% > "%OUT%"
cd /d "%~dp0SWEETs.DwgPatch.Tests"
echo. >> "%OUT%"
echo ===== build ===== >> "%OUT%"
dotnet build -c Release --nologo -v q >> "%OUT%" 2>&1
if errorlevel 1 goto end
echo. >> "%OUT%"
echo ===== reference cases ===== >> "%OUT%"
"bin\Release\SWEETs.DwgPatch.Tests.exe" >> "%OUT%" 2>&1
echo. >> "%OUT%"
echo ===== read-only scan of project drawings ===== >> "%OUT%"
"bin\Release\SWEETs.DwgPatch.Tests.exe" scan "E:\SOLIDWORKS Electrical Data\Projects" >> "%OUT%" 2>&1
:end
echo. >> "%OUT%"
echo ===== finished ===== >> "%OUT%"
endlocal
