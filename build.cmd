@echo off
cd /d "%~dp0"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

rem Generate the cat file icon once (delete cat.ico to regenerate it).
if not exist cat.ico (
    %CSC% /nologo /out:MakeIcon.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll MakeIcon.cs CatWalker.cs || exit /b 1
    .\MakeIcon.exe cat.ico || exit /b 1
    del MakeIcon.exe
)

%CSC% /nologo /target:winexe /optimize+ /win32icon:cat.ico /out:CatLock.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll CatLock.cs CatWalker.cs
