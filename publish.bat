@echo off
setlocal enabledelayedexpansion

rem ============================================================================
rem  RaceTrade - produce a SHIPPABLE build.
rem
rem  Why this script exists:
rem    bin\Release\ is the BUILD output. .NET always writes loose DLLs there - that
rem    is not a bug and it is not what you ship. The single self-contained file is
rem    produced by `dotnet publish`, which is what this script runs.
rem
rem  Result:  Release\win-x64\RaceTrade.exe     (one file, no .NET install needed)
rem           Release\linux-x64\RaceTrade       (one file, same deal)
rem ============================================================================

set ROOT=%~dp0
set OUT=%ROOT%Release
set WORK=%TEMP%\RaceTrade-publish-%RANDOM%-%RANDOM%
set PROJ=%ROOT%RaceTrade.Web\RaceTrade.Web.csproj

rem Clean old one-folder WinForms release files that may still sit directly under
rem Release\. The v2 release lives only in Release\win-x64 and Release\linux-x64.
if exist "%OUT%" del /q "%OUT%\*" 2>nul

where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo   dotnet SDK not found on PATH.
    echo   Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    exit /b 1
)

echo.
echo === Restoring ===
dotnet restore "%PROJ%" || exit /b 1

for %%R in (win-x64 linux-x64) do (
    echo.
    echo === Publishing %%R ===

    rem Wiped first: publish does not delete files from an earlier run, so a stale
    rem DLL from a previous non-single-file build would ride along in the folder.
    if exist "%OUT%\%%R" rd /s /q "%OUT%\%%R"
    if exist "%WORK%\bin\%%R" rd /s /q "%WORK%\bin\%%R"

    dotnet publish "%PROJ%" ^
        -c Release ^
        -r %%R ^
        --self-contained true ^
        -p:BaseOutputPath="%WORK%\bin\%%R\\" ^
        -p:PublishSingleFile=true ^
        -p:IncludeAllContentForSelfExtract=true ^
        -p:EnableCompressionInSingleFile=true ^
        -p:PublishTrimmed=false ^
        -p:DebugType=none ^
        -o "%OUT%\%%R" || exit /b 1

    rem Keep the release folder to the executable only. Defaults, static assets and
    rem appsettings are bundled into the single file. If an advanced user wants to
    rem override settings, they can create appsettings.json next to the exe later.
    del /q "%OUT%\%%R\*.pdb" 2>nul
    del /q "%OUT%\%%R\appsettings*.json" 2>nul
    del /q "%OUT%\%%R\*.staticwebassets*.json" 2>nul
    del /q "%OUT%\%%R\web.config" 2>nul
    if exist "%OUT%\%%R\wwwroot" rd /s /q "%OUT%\%%R\wwwroot"
    if exist "%OUT%\%%R\bin" rd /s /q "%OUT%\%%R\bin"
)

echo.
echo === Done ===
echo.
dir /b "%OUT%\win-x64"
echo.
echo   Windows : Release\win-x64\RaceTrade.exe
echo   Linux   : Release\linux-x64\RaceTrade   (chmod +x it after copying)
echo.
echo   Ship only the per-platform executable.
echo   The data\ folder is created on first run next to the executable.
echo.
if exist "%WORK%" rd /s /q "%WORK%"
endlocal
