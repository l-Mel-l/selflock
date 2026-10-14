@echo off
rem Builds SelfLock into a single exe that does not need .NET installed.
cd /d "%~dp0"
dotnet publish src\SelfLock.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish-tmp
if errorlevel 1 exit /b 1
if not exist dist mkdir dist
copy /y publish-tmp\SelfLock.exe dist\SelfLock.exe >nul
rmdir /s /q publish-tmp
echo Done: dist\SelfLock.exe
