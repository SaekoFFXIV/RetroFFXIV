@echo off
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64
cd /d "%~dp0"
cl /O2 /LD /EHsc /MT /I. snes_h264.cpp /Fe:snes_h264.dll
if %ERRORLEVEL% EQU 0 (
    echo.
    echo Build succeeded: snes_h264.dll
    del /q snes_h264.obj snes_h264.exp snes_h264.lib 2>nul
) else (
    echo.
    echo BUILD FAILED
)
