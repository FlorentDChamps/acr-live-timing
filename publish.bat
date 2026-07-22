@echo off
dotnet publish ACRLiveTiming.csproj ^
  -c Release ^
  -r win-x64 ^
  --no-self-contained ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
rem Trailing backslash forces "copy" to treat the target as a directory, so a
rem missing publish\ errors out instead of becoming a file of concatenated READMEs.
copy /y README*.md publish\ >nul
echo.
echo Done. Output in .\publish\
