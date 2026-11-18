@echo off
rem Sonar Discord Mute - derleme (Windows'ta yerlesik .NET Framework 4 derleyicisi kullanilir, ek kurulum gerekmez)
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /out:SonarDiscordMute.exe /win32manifest:app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll SonarDiscordMute.cs
if errorlevel 1 (
    echo.
    echo DERLEME BASARISIZ
    pause
    exit /b 1
)
echo Tamam: %~dp0SonarDiscordMute.exe
