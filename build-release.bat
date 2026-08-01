@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
set "SOLUTION=%ROOT%RaceTrade.sln"
set "PROJECT_DIR=%ROOT%RaceTrade"
set "ASSEMBLY_INFO=%PROJECT_DIR%\Properties\AssemblyInfo.cs"
set "RAR_EXE=C:\Program Files\WinRAR\Rar.exe"
set "WORK_DIR=%ROOT%work"

set "CONFIGURATION=Release"
set "PLATFORM=x64"

if not "%~1"=="" set "CONFIGURATION=%~1"
if not "%~2"=="" set "PLATFORM=%~2"

if /I "%~1"=="--install-build-tools" goto install_build_tools_only

if not exist "%SOLUTION%" goto missing_solution
if not exist "%ASSEMBLY_INFO%" goto missing_assembly_info

call :find_msbuild
if errorlevel 1 exit /b %errorlevel%

set "VERSION="
for /f "tokens=2 delims=()" %%V in ('findstr /r /c:"AssemblyVersion" "%ASSEMBLY_INFO%"') do set "VERSION=%%~V"
if "%VERSION%"=="" goto missing_version

if /I "%CONFIGURATION%"=="Release" if not exist "%RAR_EXE%" goto missing_rar

echo RaceTrade build
echo   Version:       %VERSION%
echo   Configuration: %CONFIGURATION%
echo   Platform:      %PLATFORM%
echo   MSBuild:       %MSBUILD%
echo.

"%MSBUILD%" "%SOLUTION%" /t:Build /p:Configuration=%CONFIGURATION% /p:Platform=%PLATFORM% /verbosity:minimal
if errorlevel 1 exit /b %errorlevel%

if /I "%PLATFORM%"=="AnyCPU" goto bin_anycpu
set "BIN_DIR=%PROJECT_DIR%\bin\%PLATFORM%\%CONFIGURATION%"
set "RAR_FILE=%PROJECT_DIR%\bin\%PLATFORM%\RaceTrade_%VERSION%.rar"
goto bin_done

:bin_anycpu
set "BIN_DIR=%PROJECT_DIR%\bin\%CONFIGURATION%"
set "RAR_FILE=%PROJECT_DIR%\bin\RaceTrade_%VERSION%.rar"

:bin_done
set "EXE_FILE=%BIN_DIR%\RaceTrade.exe"
if not exist "%EXE_FILE%" goto missing_exe
echo Built EXE: %EXE_FILE%

if /I not "%CONFIGURATION%"=="Release" exit /b 0
if not exist "%RAR_FILE%" goto missing_rar_output
echo Built RAR: %RAR_FILE%
exit /b 0

:find_msbuild
if "%MSBUILD_PATH%"=="" goto find_known_msbuild
set "MSBUILD=%MSBUILD_PATH%"
if exist "%MSBUILD%" exit /b 0
echo MSBUILD_PATH does not exist: %MSBUILD%
exit /b 1

:find_known_msbuild
call :search_msbuild
if not errorlevel 1 exit /b 0

if /I "%RACETRADE_NO_BUILDTOOLS_INSTALL%"=="1" goto missing_msbuild

echo MSBuild.exe was not found.
echo Installing Visual Studio Build Tools now...
call :install_build_tools
if errorlevel 1 exit /b %errorlevel%

call :search_msbuild
if not errorlevel 1 exit /b 0

echo MSBuild.exe is still missing after Build Tools install.
echo A reboot may be required. Run build-release.bat again after reboot.
exit /b 1

:search_msbuild
set "MSBUILD=C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
if exist "%MSBUILD%" exit /b 0
set "MSBUILD=C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe"
if exist "%MSBUILD%" exit /b 0
set "MSBUILD=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe"
if exist "%MSBUILD%" exit /b 0
set "MSBUILD=C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
if exist "%MSBUILD%" exit /b 0

for /f "delims=" %%M in ('where MSBuild.exe 2^>nul') do (
    set "MSBUILD=%%M"
    exit /b 0
)

exit /b 1

:install_build_tools_only
call :install_build_tools
exit /b %errorlevel%

:install_build_tools
call :ensure_admin
if errorlevel 1 exit /b %errorlevel%

set "VS_BOOTSTRAPPER_URL=https://aka.ms/vs/17/release/vs_BuildTools.exe"
set "VS_BOOTSTRAPPER=%WORK_DIR%\vs_BuildTools.exe"
set "VS_INSTALL_PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools"

if not exist "%WORK_DIR%" mkdir "%WORK_DIR%"

echo Checking Visual Studio Build Tools bootstrapper...
call :download_file "%VS_BOOTSTRAPPER_URL%" "%VS_BOOTSTRAPPER%"
if errorlevel 1 exit /b %errorlevel%

echo Running Visual Studio Build Tools installer...
echo This installs: Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools
start /wait "" "%VS_BOOTSTRAPPER%" --installPath "%VS_INSTALL_PATH%" --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --passive --wait --norestart
set "VS_INSTALL_EXIT=%ERRORLEVEL%"

if "%VS_INSTALL_EXIT%"=="0" exit /b 0
if "%VS_INSTALL_EXIT%"=="3010" goto build_tools_restart_required

echo Visual Studio Build Tools installer failed with exit code %VS_INSTALL_EXIT%.
exit /b %VS_INSTALL_EXIT%

:build_tools_restart_required
echo Visual Studio Build Tools installed, but Windows wants a reboot.
echo Reboot and run build-release.bat again.
exit /b 0

:ensure_admin
net session >nul 2>&1
if not errorlevel 1 exit /b 0

echo Visual Studio Build Tools install needs administrator rights.
echo Requesting UAC elevation...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '--install-build-tools' -WorkingDirectory '%CD%' -Verb RunAs"
if errorlevel 1 goto admin_elevation_failed
echo Continue in the elevated window, then run build-release.bat again.
exit /b 1

:admin_elevation_failed
echo Could not request elevation. Right-click build-release.bat and choose Run as administrator.
exit /b 1

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
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri '!DOWNLOAD_URL!' -OutFile '!DOWNLOAD_OUT!'"
if errorlevel 1 goto download_failed
if exist "!DOWNLOAD_OUT!" exit /b 0

:download_failed
echo Failed to download: !DOWNLOAD_URL!
exit /b 1

:download_cached
echo Using cached download: !DOWNLOAD_OUT!
exit /b 0

:missing_solution
echo Solution not found: %SOLUTION%
exit /b 1

:missing_assembly_info
echo AssemblyInfo.cs not found: %ASSEMBLY_INFO%
exit /b 1

:missing_version
echo Could not read AssemblyVersion from: %ASSEMBLY_INFO%
exit /b 1

:missing_rar
echo WinRAR Rar.exe not found: %RAR_EXE%
exit /b 1

:missing_exe
echo Build finished, but RaceTrade.exe was not found: %EXE_FILE%
exit /b 1

:missing_rar_output
echo Release build finished, but RAR was not found: %RAR_FILE%
exit /b 1

:missing_msbuild
echo MSBuild.exe was not found.
echo Automatic Build Tools install was disabled by RACETRADE_NO_BUILDTOOLS_INSTALL=1.
echo Clear that variable or install Visual Studio Build Tools manually.
exit /b 1
