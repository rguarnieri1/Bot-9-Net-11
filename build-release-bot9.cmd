@echo off
cd /d "%~dp0"
set LOG=%~dp0build-release-log.txt
echo === BUILD RELEASE Bot 9 net11 %DATE% %TIME% === > "%LOG%"
dotnet --version >> "%LOG%" 2>&1
dotnet build "Bot 9.csproj" -c Release -f net11.0 -nologo >> "%LOG%" 2>&1
if errorlevel 1 (echo BUILD FALLITA >> "%LOG%") else (echo BUILD OK >> "%LOG%")
echo FINE >> "%LOG%"
type "%LOG%"
timeout /t 5
