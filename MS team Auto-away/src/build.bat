@echo off
rem Build MSTeamsAutoAway.exe using the C# compiler that ships with Windows (.NET Framework 4.x)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /optimize+ /win32icon:app.ico /out:..\MSTeamsAutoAway.exe AutoAway.cs
