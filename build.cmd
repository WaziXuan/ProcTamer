@echo off
rem 用系统自带的 .NET Framework 编译器生成 ProcTamer.exe，不需要安装任何开发环境
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 ^
  /codepage:65001 /win32manifest:app.manifest /out:ProcTamer.exe ^
  /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll ProcTamer.cs
