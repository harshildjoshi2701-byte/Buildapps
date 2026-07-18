@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM  Bank Reconciliation Tool - Build Script
REM
REM  Restores, builds, runs unit tests, publishes a self-contained single-file
REM  Windows executable, and (if Inno Setup is installed) builds the
REM  Setup.exe installer.
REM
REM  Requirements:
REM    - .NET 8 SDK   https://dotnet.microsoft.com/download/dotnet/8.0
REM    - Inno Setup 6 https://jrsoftware.org/isdl.php   (optional - installer only)
REM
REM  Usage:
REM    build.bat                 build + test + publish + installer
REM    build.bat /notests        skip the unit test run
REM    build.bat /noinstaller    skip the Inno Setup step
REM ============================================================================

cd /d "%~dp0"
set SOLUTION=BankReconciliation.sln
set APP_PROJECT=src\BankReconciliation.App\BankReconciliation.App.csproj
set TEST_PROJECT=src\BankReconciliation.Core.Tests\BankReconciliation.Core.Tests.csproj
set DIST_DIR=%~dp0dist
set APP_DIST_DIR=%DIST_DIR%\app
set INSTALLER_DIST_DIR=%DIST_DIR%\installer
set RUN_TESTS=1
set RUN_INSTALLER=1

for %%A in (%*) do (
    if /I "%%A"=="/notests" set RUN_TESTS=0
    if /I "%%A"=="/noinstaller" set RUN_INSTALLER=0
)

echo.
echo ================================================================
echo  Bank Reconciliation Tool - Build
echo ================================================================
echo.

REM ---- 0. Check for .NET 8 SDK ----------------------------------------------
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET SDK was not found on PATH.
    echo         Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    echo         then re-run this script.
    exit /b 1
)

dotnet --list-sdks | findstr /R "^8\." >nul
if errorlevel 1 (
    echo [WARNING] No .NET 8.x SDK was detected via "dotnet --list-sdks".
    echo           This project targets net8.0 / net8.0-windows. If the build
    echo           below fails, install the .NET 8 SDK and try again.
    echo.
)

REM ---- 1. Restore -------------------------------------------------------------
echo [1/5] Restoring NuGet packages...
dotnet restore "%SOLUTION%"
if errorlevel 1 goto :error

REM ---- 2. Build (Release, compiles all 3 projects) -----------------------------
echo.
echo [2/5] Building solution (Release)...
dotnet build "%SOLUTION%" -c Release --no-restore
if errorlevel 1 goto :error

REM ---- 3. Unit tests ------------------------------------------------------------
if "%RUN_TESTS%"=="1" (
    echo.
    echo [3/5] Running unit tests...
    dotnet test "%TEST_PROJECT%" -c Release --no-build --logger "console;verbosity=normal"
    if errorlevel 1 (
        echo.
        echo [WARNING] One or more unit tests failed. Continuing to publish the
        echo           app anyway so you can inspect the build, but please review
        echo           the test output above before relying on this build.
        echo.
    )
) else (
    echo.
    echo [3/5] Skipping unit tests ^(/notests^).
)

REM ---- 4. Publish self-contained single-file exe ---------------------------------
echo.
echo [4/5] Publishing self-contained win-x64 executable...
if exist "%APP_DIST_DIR%" rmdir /s /q "%APP_DIST_DIR%"
dotnet publish "%APP_PROJECT%" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -o "%APP_DIST_DIR%"
if errorlevel 1 goto :error

echo.
echo         Published: %APP_DIST_DIR%\BankReconciliation.exe

REM ---- 5. Installer (Inno Setup) ---------------------------------------------------
if "%RUN_INSTALLER%"=="0" (
    echo.
    echo [5/5] Skipping installer build ^(/noinstaller^).
    goto :done
)

echo.
echo [5/5] Building installer...
set ISCC=
where iscc >nul 2>nul
if not errorlevel 1 (
    set ISCC=iscc
) else (
    if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set ISCC="%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
    if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set ISCC="%ProgramFiles%\Inno Setup 6\ISCC.exe"
)

if "%ISCC%"=="" (
    echo [WARNING] Inno Setup ^(ISCC.exe^) was not found, so no installer was built.
    echo           Download Inno Setup 6 from https://jrsoftware.org/isdl.php,
    echo           then either re-run build.bat or open installer\setup.iss in
    echo           the Inno Setup Compiler and click Build.
    echo           The standalone app itself is still fully usable - see
    echo           %APP_DIST_DIR%\BankReconciliation.exe
    goto :done
)

if not exist "%INSTALLER_DIST_DIR%" mkdir "%INSTALLER_DIST_DIR%"
%ISCC% /O"%INSTALLER_DIST_DIR%" "installer\setup.iss"
if errorlevel 1 goto :error

echo.
echo         Installer built in: %INSTALLER_DIST_DIR%

:done
echo.
echo ================================================================
echo  Build complete.
echo    Standalone exe : %APP_DIST_DIR%\BankReconciliation.exe
echo    Installer      : %INSTALLER_DIST_DIR%  ^(if built^)
echo ================================================================
exit /b 0

:error
echo.
echo ================================================================
echo  BUILD FAILED - see errors above.
echo ================================================================
exit /b 1
