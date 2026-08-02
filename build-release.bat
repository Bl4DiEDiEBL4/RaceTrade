@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
set "SOLUTION=!ROOT!RaceTrade.sln"
set "PROJECT_DIR=!ROOT!RaceTrade"
set "ASSEMBLY_INFO=!PROJECT_DIR!\Properties\AssemblyInfo.cs"
set "PACKAGES_CONFIG=!PROJECT_DIR!\Resources\packages.config"
set "PACKAGES_DIR=!ROOT!packages"
set "RAR_EXE=C:\Program Files\WinRAR\Rar.exe"
set "WORK_DIR=!ROOT!work"
set "NUGET_EXE=!WORK_DIR!\nuget.exe"

set "CONFIGURATION=Release"
set "PLATFORM=x64"

if /I "%~1"=="--install-build-tools" goto install_build_tools_only
if /I "%~1"=="--restore" goto restore_only
if /I "%~1"=="--unblock" goto unblock_only
if /I "%~1"=="--self-test" goto self_test

if not "%~1"=="" set "CONFIGURATION=%~1"
if not "%~2"=="" set "PLATFORM=%~2"

if not exist "!SOLUTION!" goto missing_solution
if not exist "!ASSEMBLY_INFO!" goto missing_assembly_info

call :find_msbuild
if errorlevel 1 exit /b !errorlevel!

call :unblock_sources
if errorlevel 1 exit /b !errorlevel!

call :restore_packages
if errorlevel 1 exit /b !errorlevel!

call :read_version
if errorlevel 1 exit /b !errorlevel!

if /I "!CONFIGURATION!"=="Release" if not exist "!RAR_EXE!" goto missing_rar

if not exist "!WORK_DIR!" mkdir "!WORK_DIR!"
set "BUILD_LOG=!WORK_DIR!\msbuild.log"

echo RaceTrade build
echo   Version:       !VERSION!
echo   Configuration: !CONFIGURATION!
echo   Platform:      !PLATFORM!
echo   MSBuild:       !MSBUILD!
echo   Log:           !BUILD_LOG!
echo.

REM Always write a detailed log. With only /verbosity:minimal on the console the
REM actual error can scroll past (or vanish when the window closes on a
REM double-click), which makes a failed build impossible to diagnose.
REM Keep manifest signing disabled from the command line too. Older local files
REM or stale project metadata can otherwise fail with "MSB3323: Unable to find
REM manifest signing certificate", while GenerateManifests is false anyway.
"!MSBUILD!" "!SOLUTION!" /t:Build /p:Configuration=!CONFIGURATION! /p:Platform=!PLATFORM! /p:SignManifests=false /nologo /verbosity:minimal /m /fl "/flp:LogFile=!BUILD_LOG!;Verbosity=detailed;Encoding=UTF-8"
set "BUILD_EXIT=!ERRORLEVEL!"
if not "!BUILD_EXIT!"=="0" goto build_failed

if /I "!PLATFORM!"=="AnyCPU" goto bin_anycpu
set "BIN_DIR=!PROJECT_DIR!\bin\!PLATFORM!\!CONFIGURATION!"
set "RAR_FILE=!PROJECT_DIR!\bin\!PLATFORM!\RaceTrade_!VERSION!.rar"
goto bin_done

:bin_anycpu
set "BIN_DIR=!PROJECT_DIR!\bin\!CONFIGURATION!"
set "RAR_FILE=!PROJECT_DIR!\bin\RaceTrade_!VERSION!.rar"

:bin_done
set "EXE_FILE=!BIN_DIR!\RaceTrade.exe"
if not exist "!EXE_FILE!" goto missing_exe
echo Built EXE: !EXE_FILE!

if /I not "!CONFIGURATION!"=="Release" exit /b 0

call :copy_to_release
if errorlevel 1 exit /b !errorlevel!

if not exist "!RAR_FILE!" goto missing_rar_output
echo Built RAR: !RAR_FILE!
exit /b 0

REM ---------------------------------------------------------------------------
REM Copy the distributable output to <root>\Release
REM ---------------------------------------------------------------------------

:copy_to_release
if /I "!RACETRADE_NO_RELEASE_COPY!"=="1" exit /b 0

set "RELEASE_DIR=!ROOT!Release"

REM Only the redistributable files are copied. The bin folder ALSO contains the
REM live runtime data (sites\, cbftp\, settings\, db\, pre_bots\) which holds
REM site logins and cbftp passwords; copying those into a folder that gets
REM zipped and shared would leak credentials. The app recreates these folders on
REM first start, so they are not needed for distribution.
if not exist "!RELEASE_DIR!" mkdir "!RELEASE_DIR!"

copy /y "!BIN_DIR!\RaceTrade.exe" "!RELEASE_DIR!\" >nul
if errorlevel 1 goto release_copy_failed

if exist "!BIN_DIR!\RaceTrade.exe.config" (
    copy /y "!BIN_DIR!\RaceTrade.exe.config" "!RELEASE_DIR!\" >nul
    if errorlevel 1 goto release_copy_failed
)

for %%D in ("!BIN_DIR!\*.dll") do (
    copy /y "%%~fD" "!RELEASE_DIR!\" >nul
    if errorlevel 1 goto release_copy_failed
)

echo Copied release files to: !RELEASE_DIR!
exit /b 0

:release_copy_failed
echo Failed to copy build output to: !RELEASE_DIR!
exit /b 1

:build_failed
echo.
echo ===========================================================
echo  BUILD FAILED  ^(MSBuild exit code !BUILD_EXIT!^)
echo ===========================================================
if not exist "!BUILD_LOG!" goto build_failed_nolog

set /a ERR_SHOWN=0
echo Errors found in the build log:
echo.
for /f "usebackq tokens=* delims=" %%L in (`findstr /i /r /c:"error [A-Z]*[0-9]" /c:"error :" "!BUILD_LOG!" 2^>nul`) do (
    set /a ERR_SHOWN+=1
    if !ERR_SHOWN! leq 25 echo   %%L
)

if !ERR_SHOWN!==0 (
    echo   ^(no lines matched "error"; showing the last lines of the log^)
    echo.
    for /f "usebackq tokens=* delims=" %%L in (`powershell -NoProfile -Command "Get-Content -LiteralPath '!BUILD_LOG!' -Tail 25" 2^>nul`) do echo   %%L
) else (
    if !ERR_SHOWN! gtr 25 echo   ... and !ERR_SHOWN! errors in total.
)

echo.
echo Full log: !BUILD_LOG!
echo.
echo Common causes:
echo   - MSB3821 mark-of-the-web  ^: run build-release.bat --unblock
echo   - Missing NuGet packages   ^: run build-release.bat --restore
echo   - Missing .NET Framework 4.8 targeting pack ^: rerun --install-build-tools
echo   - Antivirus locking bin\ or obj\ during the build
call :pause_if_double_clicked
exit /b !BUILD_EXIT!

:build_failed_nolog
echo MSBuild produced no log file at: !BUILD_LOG!
echo Rerun and check the console output above.
call :pause_if_double_clicked
exit /b !BUILD_EXIT!

REM Keep the window open when the script was double-clicked (cmd /c), so the
REM error is readable. When run from an existing console or CI, do nothing.
:pause_if_double_clicked
echo %CMDCMDLINE% | find /i "/c" >nul
if errorlevel 1 exit /b 0
echo.
pause
exit /b 0

REM ---------------------------------------------------------------------------
REM MSBuild discovery
REM ---------------------------------------------------------------------------

:find_msbuild
if "!MSBUILD_PATH!"=="" goto find_known_msbuild
set "MSBUILD=!MSBUILD_PATH!"
if exist "!MSBUILD!" exit /b 0
echo MSBUILD_PATH does not exist: !MSBUILD!
exit /b 1

:find_known_msbuild
call :search_msbuild
if not errorlevel 1 exit /b 0

if /I "!RACETRADE_NO_BUILDTOOLS_INSTALL!"=="1" goto missing_msbuild

echo MSBuild.exe was not found.
echo Installing Visual Studio Build Tools now...
call :install_build_tools
if errorlevel 1 exit /b !errorlevel!

call :search_msbuild
if not errorlevel 1 exit /b 0

echo MSBuild.exe is still missing after Build Tools install.
echo A reboot may be required. Run build-release.bat again after reboot.
exit /b 1

:search_msbuild
call :search_msbuild_with_vswhere
if not errorlevel 1 exit /b 0

REM Probe every edition under BOTH Program Files roots. VS2022 Build Tools
REM normally land in the 64-bit root, VS2019 and some Build Tools installs in
REM the x86 root, so checking only one root silently misses a valid install.
for %%R in ("%ProgramFiles%" "%ProgramFiles(x86)%") do (
    for %%Y in (2022 2019) do (
        for %%E in (Enterprise Professional Community BuildTools) do (
            call :try_msbuild "%%~R\Microsoft Visual Studio\%%Y\%%E\MSBuild\Current\Bin\amd64\MSBuild.exe"
            if not errorlevel 1 exit /b 0
            call :try_msbuild "%%~R\Microsoft Visual Studio\%%Y\%%E\MSBuild\Current\Bin\MSBuild.exe"
            if not errorlevel 1 exit /b 0
        )
    )
)

REM Last resort: PATH. Skip the .NET Framework MSBuild (v4.0.30319) because it
REM exists on every Windows box but cannot build this solution (no VS toolchain), so
REM using it produces confusing errors instead of a clear "not installed".
for /f "delims=" %%M in ('where MSBuild.exe 2^>nul') do (
    echo %%M | find /i "\Microsoft.NET\Framework" >nul
    if errorlevel 1 (
        set "MSBUILD=%%M"
        exit /b 0
    )
)

exit /b 1

:search_msbuild_with_vswhere
call :try_vswhere "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not errorlevel 1 exit /b 0
call :try_vswhere "%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if not errorlevel 1 exit /b 0
exit /b 1

:try_vswhere
set "VSWHERE=%~1"
if not exist "!VSWHERE!" exit /b 1

REM -prerelease so a Preview-only install is still found.
for /f "usebackq delims=" %%M in (`"!VSWHERE!" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\amd64\MSBuild.exe" 2^>nul`) do (
    set "MSBUILD=%%M"
    if exist "!MSBUILD!" exit /b 0
)

for /f "usebackq delims=" %%M in (`"!VSWHERE!" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2^>nul`) do (
    set "MSBUILD=%%M"
    if exist "!MSBUILD!" exit /b 0
)

exit /b 1

:try_msbuild
set "MSBUILD_CANDIDATE=%~1"
if not exist "!MSBUILD_CANDIDATE!" exit /b 1
set "MSBUILD=!MSBUILD_CANDIDATE!"
exit /b 0

:read_version
set "VERSION="
for /f "tokens=2 delims=()" %%V in ('findstr /r /c:"AssemblyVersion" "!ASSEMBLY_INFO!"') do set "VERSION=%%~V"
if not "!VERSION!"=="" exit /b 0
echo Could not read AssemblyVersion from: !ASSEMBLY_INFO!
exit /b 1

REM ---------------------------------------------------------------------------
REM Mark of the Web
REM ---------------------------------------------------------------------------

:unblock_only
call :unblock_sources
exit /b !errorlevel!

:unblock_sources
if /I "!RACETRADE_NO_UNBLOCK!"=="1" goto unblock_skipped

REM When the repo is downloaded as a ZIP, every extracted file carries a
REM Zone.Identifier ("mark of the web"). MSBuild then refuses to compile .resx
REM files with "error MSB3821: ... in the Internet or Restricted zone", which is
REM the single most common reason a fresh download fails to build.
REM Unblock-File only strips that marker; it changes no file content.
echo Removing mark-of-the-web from source files...
set "RACETRADE_UNBLOCK_ROOT=!ROOT!"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
    "$r = $env:RACETRADE_UNBLOCK_ROOT; Get-ChildItem -LiteralPath $r -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(work|\.git)\\' } | Unblock-File -ErrorAction SilentlyContinue" 2>nul

exit /b 0

:unblock_skipped
echo Skipping mark-of-the-web removal ^(RACETRADE_NO_UNBLOCK=1^).
exit /b 0

REM ---------------------------------------------------------------------------
REM NuGet restore
REM ---------------------------------------------------------------------------

:restore_only
call :find_msbuild
if errorlevel 1 exit /b !errorlevel!
call :restore_packages
exit /b !errorlevel!

:restore_packages
if /I "!RACETRADE_NO_RESTORE!"=="1" goto restore_skipped

REM This is a packages.config project: the .csproj hard-fails when
REM packages\Costura.Fody\... is missing, so packages must be on disk before
REM MSBuild runs. MSBuild /t:Restore does NOT handle packages.config, hence
REM nuget.exe.
if not exist "!WORK_DIR!" mkdir "!WORK_DIR!"

call :ensure_nuget
if errorlevel 1 exit /b !errorlevel!

echo Restoring NuGet packages...
"!NUGET_EXE!" restore "!SOLUTION!" -PackagesDirectory "!PACKAGES_DIR!" -NonInteractive
if errorlevel 1 goto restore_failed

REM packages.config lives under Resources\, which solution-level restore does
REM not always pick up, so restore it explicitly as well.
if exist "!PACKAGES_CONFIG!" (
    "!NUGET_EXE!" restore "!PACKAGES_CONFIG!" -PackagesDirectory "!PACKAGES_DIR!" -NonInteractive
    if errorlevel 1 goto restore_failed
)

echo NuGet restore complete.
exit /b 0

:restore_skipped
echo Skipping NuGet restore ^(RACETRADE_NO_RESTORE=1^).
exit /b 0

:restore_failed
echo NuGet restore failed.
exit /b 1

:ensure_nuget
if exist "!NUGET_EXE!" exit /b 0

where nuget.exe >nul 2>&1
if not errorlevel 1 (
    for /f "delims=" %%N in ('where nuget.exe 2^>nul') do (
        set "NUGET_EXE=%%N"
        exit /b 0
    )
)

echo Downloading nuget.exe...
call :download_file "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" "!NUGET_EXE!"
if errorlevel 1 exit /b 1
exit /b 0

REM ---------------------------------------------------------------------------
REM Visual Studio Build Tools install
REM ---------------------------------------------------------------------------

:install_build_tools_only
call :install_build_tools
exit /b !errorlevel!

:install_build_tools
call :search_msbuild
if not errorlevel 1 goto build_tools_already_installed

call :ensure_admin
if errorlevel 1 exit /b !errorlevel!

set "VS_BOOTSTRAPPER_URL=https://aka.ms/vs/17/release/vs_BuildTools.exe"
set "VS_BOOTSTRAPPER=!WORK_DIR!\vs_BuildTools.exe"

if not exist "!WORK_DIR!" mkdir "!WORK_DIR!"

echo Checking Visual Studio Build Tools bootstrapper...
call :download_file "!VS_BOOTSTRAPPER_URL!" "!VS_BOOTSTRAPPER!"
if errorlevel 1 exit /b !errorlevel!

echo Running Visual Studio Build Tools installer...
echo This is Build Tools only, not the Visual Studio IDE.
echo Installing: managed desktop build tools + .NET Framework 4.8 targeting pack.
echo.

REM No --installPath: let the installer use its own default location, which is
REM also where vswhere reports it. Forcing a path (especially the x86 one) is
REM how an install ends up somewhere the discovery step never looks.
REM The 4.8 targeting pack is required; without it the build fails with
REM "reference assemblies for .NETFramework,Version=v4.8 were not found".
start /wait "" "!VS_BOOTSTRAPPER!" ^
    --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools ^
    --add Microsoft.Net.Component.4.8.SDK ^
    --add Microsoft.Net.Component.4.8.TargetingPack ^
    --add Microsoft.VisualStudio.Component.NuGet ^
    --passive --wait --norestart
set "VS_INSTALL_EXIT=!ERRORLEVEL!"

if "!VS_INSTALL_EXIT!"=="0" exit /b 0
if "!VS_INSTALL_EXIT!"=="3010" goto build_tools_restart_required

echo Visual Studio Build Tools installer failed with exit code !VS_INSTALL_EXIT!.
exit /b !VS_INSTALL_EXIT!

:build_tools_already_installed
echo MSBuild.exe was found. Skipping Visual Studio Build Tools installer.
echo MSBuild: !MSBUILD!
exit /b 0

:build_tools_restart_required
echo Visual Studio Build Tools installed, but Windows wants a reboot.
echo Reboot and run build-release.bat again.
exit /b 0

:ensure_admin
net session >nul 2>&1
if not errorlevel 1 exit /b 0

echo Visual Studio Build Tools install needs administrator rights.
echo Requesting UAC elevation...
set "RACETRADE_ELEVATE_SCRIPT=%~f0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:RACETRADE_ELEVATE_SCRIPT -ArgumentList '--install-build-tools' -Verb RunAs"
if errorlevel 1 goto admin_elevation_failed
echo Continue in the elevated window, then run build-release.bat again.
exit /b 1

:admin_elevation_failed
echo Could not request elevation. Right-click build-release.bat and choose Run as administrator.
exit /b 1

REM ---------------------------------------------------------------------------
REM Helpers
REM ---------------------------------------------------------------------------

:download_file
set "DOWNLOAD_URL=%~1"
set "DOWNLOAD_OUT=%~2"
set "DOWNLOAD_SIZE=0"

if not exist "!DOWNLOAD_OUT!" goto download_missing
for %%F in ("!DOWNLOAD_OUT!") do set "DOWNLOAD_SIZE=%%~zF"
if not "!DOWNLOAD_SIZE!"=="0" goto download_cached
del /f /q "!DOWNLOAD_OUT!" >nul 2>&1

:download_missing
where curl.exe >nul 2>&1
if errorlevel 1 goto download_with_powershell

echo Downloading: !DOWNLOAD_URL!
curl.exe -L --fail --output "!DOWNLOAD_OUT!" "!DOWNLOAD_URL!"
if not errorlevel 1 if exist "!DOWNLOAD_OUT!" exit /b 0

:download_with_powershell
echo Downloading: !DOWNLOAD_URL!
set "RACETRADE_DOWNLOAD_URL=!DOWNLOAD_URL!"
set "RACETRADE_DOWNLOAD_OUT=!DOWNLOAD_OUT!"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri $env:RACETRADE_DOWNLOAD_URL -OutFile $env:RACETRADE_DOWNLOAD_OUT"
if errorlevel 1 goto download_failed
if exist "!DOWNLOAD_OUT!" exit /b 0

:download_failed
echo Failed to download: !DOWNLOAD_URL!
exit /b 1

:download_cached
echo Using cached download: !DOWNLOAD_OUT!
exit /b 0

:self_test
echo Running build-release.bat self-test...
set "TEST_DIR=!WORK_DIR!\self test (1)"
set "TEST_FILE=!TEST_DIR!\vs_BuildTools.exe"

if not exist "!TEST_DIR!" mkdir "!TEST_DIR!"
> "!TEST_FILE!" echo cached
call :download_file "https://example.invalid/not-used.exe" "!TEST_FILE!"
if errorlevel 1 goto self_test_failed

del /f /q "!TEST_FILE!" >nul 2>&1
rd "!TEST_DIR!" >nul 2>&1

echo Checking MSBuild discovery...
call :search_msbuild
if errorlevel 1 (
    echo   MSBuild: NOT FOUND ^(build-release.bat would install Build Tools^)
) else (
    echo   MSBuild: !MSBUILD!
)

echo Self-test OK.
exit /b 0

:self_test_failed
echo Self-test failed.
exit /b 1

:missing_solution
echo Solution not found: !SOLUTION!
exit /b 1

:missing_assembly_info
echo AssemblyInfo.cs not found: !ASSEMBLY_INFO!
exit /b 1

:missing_rar
echo WinRAR Rar.exe not found: !RAR_EXE!
echo Install WinRAR, or set RAR_EXE in this script to your Rar.exe path.
echo A non-Release build ^(build-release.bat Debug^) does not need WinRAR.
exit /b 1

:missing_exe
echo Build finished, but RaceTrade.exe was not found: !EXE_FILE!
exit /b 1

:missing_rar_output
echo Release build finished, but RAR was not found: !RAR_FILE!
exit /b 1

:missing_msbuild
echo MSBuild.exe was not found.
echo Automatic Build Tools install was disabled by RACETRADE_NO_BUILDTOOLS_INSTALL=1.
echo Clear that variable or install Visual Studio Build Tools manually.
exit /b 1
