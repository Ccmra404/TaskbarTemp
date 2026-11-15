@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "MSVC_DIR="
set "VS_INSTALL="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS_INSTALL=%%I"
if defined VS_INSTALL for /f "delims=" %%D in ('dir /b /ad "%VS_INSTALL%\VC\Tools\MSVC" 2^>nul ^| sort') do set "MSVC_DIR=%VS_INSTALL%\VC\Tools\MSVC\%%D"
if not defined MSVC_DIR (
  echo [error] MSVC toolset not found
  exit /b 1
)

set "SDK_VER="
for /f "delims=" %%V in ('dir /b /ad "C:\Program Files (x86)\Windows Kits\10\Include" 2^>nul ^| findstr /r "^[0-9]"') do set "SDK_VER=%%V"
if not defined SDK_VER (
  echo [error] Windows SDK not found
  exit /b 1
)
set "SDK=C:\Program Files (x86)\Windows Kits\10"
for /f "delims=" %%B in ('dir /b /ad "%SDK%\bin" ^| findstr /r "10\." ^| sort 2^>nul') do set "SDK_BIN_VER=%%B"
if not defined SDK_BIN_VER set "SDK_BIN_VER=%SDK_VER%"

set "INCLUDE=%MSVC_DIR%\include;%SDK%\Include\%SDK_VER%\ucrt;%SDK%\Include\%SDK_VER%\um;%SDK%\Include\%SDK_VER%\shared;%SDK%\Include\%SDK_VER%\winrt;%SDK%\Include\%SDK_VER%\cppwinrt"
set "LIB=%MSVC_DIR%\lib\x64;%SDK%\Lib\%SDK_VER%\ucrt\x64;%SDK%\Lib\%SDK_VER%\um\x64"
set "PATH=%MSVC_DIR%\bin\Hostx64\x64;%SDK%\bin\%SDK_BIN_VER%\x64;%PATH%"

echo [info] MSVC: %MSVC_DIR%
echo [info] SDK : %SDK_VER%

if not exist bin mkdir bin

rc /nologo /fo bin\resource.res resource.rc || exit /b 1
cl /nologo /W4 /O2 /MT /utf-8 /EHsc taskbar_temp.cpp bin\resource.res ^
   /Fe:bin\TaskbarTempCpp.exe /Fo:bin\ /link /subsystem:windows || exit /b 1

del bin\*.obj 2>nul
echo.
echo [ok] bin\TaskbarTempCpp.exe
endlocal
