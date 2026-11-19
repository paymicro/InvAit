@echo off

for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do (
    call "%%i\Common7\Tools\VsDevCmd.bat" && msbuild "..\InvAit.csproj" /t:Rebuild /p:Configuration=Release
)

pause
