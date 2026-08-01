@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
set "SOLUTION=%ROOT%RaceTrade.sln"
set "PROJECT_DIR=%ROOT%RaceTrade"
set "ASSEMBLY_INFO=%PROJECT_DIR%\Properties\AssemblyInfo.cs"
set "RAR_EXE=C:\Program Files\WinRAR\Rar.exe"

set "CONFIGURATION=Release"
set "PLATFORM=x64"

if not "%~1"=="" set "CONFIGURATION=%~1"
if not "%~2"=="" set "PLATFORM=%~2"

if not exist "%SOLUTION%" goto missing_solution
if not exist "%ASSEMBLY_INFO%" goto missing_assembly_info
if /I "%CONFIGURATION%"=="Release" if not exist "%RAR_EXE%" goto missing_rar

call :find_msbuild
if errorlevel 1 exit /b %errorlevel%

call :read_version
if errorlevel 1 exit /b %errorlevel%

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

echo MSBuild.exe was not found. Install Visual Studio Build Tools or set MSBUILD_PATH.
exit /b 1

:read_version
set "VERSION="
for /f "tokens=2 delims=()" %%V in ('findstr /r /c:"AssemblyVersion" "!ASSEMBLY_INFO!"') do (
    set "VERSION=%%~V"
    goto version_done
)

:version_done
if not "%VERSION%"=="" exit /b 0
echo Could not read AssemblyVersion from: %ASSEMBLY_INFO%
exit /b 1

:missing_solution
echo Solution not found: %SOLUTION%
exit /b 1

:missing_assembly_info
echo AssemblyInfo.cs not found: %ASSEMBLY_INFO%
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
