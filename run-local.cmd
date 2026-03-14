@echo off
dotnet publish src\Scanner\Scanner.csproj -c Release -o publish
set IMAGES_DIR=%USERPROFILE%\OneDrive\Documents\Scanned Documents
start http://localhost:5000
dotnet publish\Scanner.dll
