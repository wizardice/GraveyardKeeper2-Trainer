# Official build script (per HANDOFF.md section 5; args MUST be passed as array; C# 5)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# Precondition: no leftover GK2Trainer process locking the output file
Get-Process GK2Trainer -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }

$cscArgs = @(
  "/nologo","/target:winexe","/codepage:65001","/optimize+","/platform:x64",
  "/win32icon:$root\assets\gk2.ico",
  "/resource:$root\assets\gk2.ico,gk2.ico",
  "/out:$root\dist\GK2Trainer.exe",
  "/r:System.dll","/r:System.Windows.Forms.dll","/r:System.Drawing.dll"
) + (Get-ChildItem "$root\src\*.cs").FullName

& $csc $cscArgs
Write-Host "EXITCODE=$LASTEXITCODE"
Get-Item "$root\dist\GK2Trainer.exe" | Select-Object Length,LastWriteTime
