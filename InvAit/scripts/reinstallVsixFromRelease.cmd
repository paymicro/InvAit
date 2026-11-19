@echo off

for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do (
    call "%%i\Common7\Tools\VsDevCmd.bat" && VSIXInstaller.exe /quiet /uninstall:InvAit.a3200071-b4f2-4ba3-a720-1fb1c0267b47 && VSIXInstaller.exe /quiet "..\bin\Release\InvAit.vsix"
)

echo DONE

pause
