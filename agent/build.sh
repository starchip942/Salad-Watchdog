#!/bin/sh
# Builds agent/GPU-Watchdog-Agent.exe (.NET Framework 4.8, built into Windows 10/11; nothing to install).
# Needs a C# compiler with the .NET Framework 4.8 reference assemblies (here: Mono's csc).
set -e
cd "$(dirname "$0")"
CSC=${CSC:-/opt/mono/bin/csc}
API=${API:-/opt/mono/lib/mono/4.8-api}
$CSC -nologo -langversion:9 -target:winexe -platform:x64 -optimize+ -nowarn:CS0168,CS0219,CS1998,CS4014 \
  -out:GPU-Watchdog-Agent.exe -win32manifest:app.manifest -win32icon:icon.ico \
  -nostdlib -r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll -r:$API/System.Windows.Forms.dll \
  -r:$API/System.Drawing.dll -r:$API/System.Web.Extensions.dll -r:$API/System.Management.dll -r:$API/System.ServiceProcess.dll \
  -r:$API/Microsoft.CSharp.dll -r:$API/Facades/System.Runtime.dll -r:$API/Facades/netstandard.dll \
  -r:lib/LibreHardwareMonitorLib.dll src/*.cs
echo built GPU-Watchdog-Agent.exe
