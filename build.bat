@echo off
setlocal EnableExtensions DisableDelayedExpansion

if /i "%~1"=="--help" goto :help
if /i "%~1"=="/?" goto :help
if not "%~1"=="" goto :invalid_arguments

:start
pushd "%~dp0"
if errorlevel 1 exit /b 1
set "RS_ROOT=%CD%"

where.exe dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: Install the .NET 8 SDK and make dotnet available on PATH.
    goto :failed
)
where.exe powershell.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: Windows PowerShell is required to create the release archive.
    goto :failed
)

set "RS_BUILD_RID=win-x64"
set "RS_BUILD_STAMP="
for /f %%I in ('powershell.exe -NoProfile -NonInteractive -Command "Get-Date -Format yyyyMMdd-HHmmss-fff"') do set "RS_BUILD_STAMP=%%I"
if not defined RS_BUILD_STAMP goto :failed
set "RS_BUILD_OUTPUT=%RS_ROOT%\artifacts\release\%RS_BUILD_STAMP%-%RS_BUILD_RID%"
if exist "%RS_BUILD_OUTPUT%" (
    echo ERROR: The release output directory already exists. Run the script again.
    goto :failed
)
mkdir "%RS_BUILD_OUTPUT%"
if errorlevel 1 goto :failed

echo [1/5] Building the application in Release...
dotnet build sources\RAMshot.csproj -c Release
if errorlevel 1 goto :failed

echo [2/5] Building scanner regression tests...
dotnet build tests\ScannerRegression\ScannerRegression.csproj -c Release
if errorlevel 1 goto :failed

echo [3/5] Running scanner regression tests...
dotnet run --project tests\ScannerRegression\ScannerRegression.csproj -c Release --no-build
if errorlevel 1 goto :failed
dotnet run --project tests\ScannerRegression\ScannerRegression.csproj -c Release --no-build -- 8
if errorlevel 1 goto :failed

echo [4/5] Publishing the single-file executable...
dotnet publish sources\RAMshot.csproj -c Release -o "%RS_BUILD_OUTPUT%"
if errorlevel 1 goto :failed

echo [5/5] Adding the license and creating the archive...
set "RS_VERSION="
for /f "tokens=2 delims=<> " %%V in ('findstr /r /c:"<Version>[0-9]" sources\RAMshot.csproj') do set "RS_VERSION=%%V"
if not defined RS_VERSION (
    echo ERROR: Cannot read the application version from sources\RAMshot.csproj.
    goto :failed
)
set "RS_ZIP=%RS_BUILD_OUTPUT%\RAMshot-%RS_VERSION%-%RS_BUILD_RID%.zip"
copy /y LICENSE "%RS_BUILD_OUTPUT%\LICENSE.txt" >nul
if errorlevel 1 goto :failed
powershell.exe -NoProfile -NonInteractive -Command "Compress-Archive -Path (Join-Path '%RS_BUILD_OUTPUT%' 'RAMshot.exe'), (Join-Path '%RS_BUILD_OUTPUT%' 'LICENSE.txt') -DestinationPath '%RS_ZIP%' -Force"
if errorlevel 1 goto :failed

echo.
echo Release artifacts are ready:
echo   %RS_BUILD_OUTPUT%\RAMshot.exe
echo   %RS_ZIP%
popd
endlocal
exit /b 0

:failed
set "RS_BUILD_EXIT=%errorlevel%"
if "%RS_BUILD_EXIT%"=="0" set "RS_BUILD_EXIT=1"
echo.
echo ERROR: Release preparation failed. See the output above.
popd
endlocal & exit /b %RS_BUILD_EXIT%

:invalid_arguments
echo ERROR: Unexpected arguments.
echo Usage: build.bat [--help]
exit /b 2

:help
echo Usage: build.bat
echo Builds Release, runs the scanner regression tests, and creates artifacts\release\TIMESTAMP-win-x64\RAMshot.exe
echo plus a ZIP archive with the executable and the MIT license.
echo Default and only target: win-x64. Requires the .NET 8 SDK and Windows PowerShell.
exit /b 0
