$root = Split-Path (Split-Path $PSScriptRoot)
$exe  = Join-Path $root 'agent\GPU-Watchdog-Agent.exe'
New-Item -ItemType Directory -Force (Join-Path $root 'logs') | Out-Null
$out  = Join-Path $root 'logs\diagnose.txt'
$r = New-Object System.Collections.Generic.List[string]
function A($s) { $r.Add([string]$s) }
A "Diagnose $(Get-Date)"
A "exe exists: $(Test-Path $exe)  size: $((Get-Item $exe -ErrorAction SilentlyContinue).Length)"
try { $z = Get-Content $exe -Stream Zone.Identifier -ErrorAction Stop; A "Zone.Identifier: $z" } catch { A "Zone.Identifier: none" }
A ".NET Framework release: $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release)"
A "Smart App Control state (0 off,1 enforce,2 eval): $((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState)"
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'GPU Watchdog.lnk'
try {
  $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); A "Shortcut target: '$($s.TargetPath)' workdir: '$($s.WorkingDirectory)'"
  if ($s.TargetPath -ne $exe) {
    # repair it the Windows way
    $s.TargetPath = $exe; $s.WorkingDirectory = Split-Path $exe; $s.IconLocation = "$exe,0"; $s.Description = 'GPU Watchdog'; $s.Save()
    $b = [IO.File]::ReadAllBytes($lnk); $b[0x15] = $b[0x15] -bor 0x20; [IO.File]::WriteAllBytes($lnk, $b)
    A "Shortcut repaired -> '$((New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath)'"
  }
} catch { A "Shortcut problem: $_" }
A "--- starting the exe (a permission prompt will appear: click Yes) ---"
try {
  $p = Start-Process $exe -ArgumentList '--selftest' -Verb RunAs -PassThru -ErrorAction Stop
  if ($p.WaitForExit(20000)) { A "selftest exit code: $($p.ExitCode)" } else { A "selftest still running after 20 s" }
} catch { A "Start failed: $($_.Exception.Message)" }
A "--- recent Application errors ---"
try {
  Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddHours(-3)} -ErrorAction Stop |
    Where-Object { $_.ProviderName -in '.NET Runtime','Application Error','Windows Error Reporting' -and $_.Message -match 'Watchdog' } |
    Select-Object -First 4 | ForEach-Object { A "[$($_.TimeCreated)] $($_.ProviderName): $($_.Message)" }
} catch { A "event log: $_" }
A "--- Defender / Smart App Control blocks ---"
try { Get-MpThreatDetection -ErrorAction Stop | Select-Object -First 5 | ForEach-Object { A "Defender: $($_.Resources) $($_.InitialDetectionTime)" } } catch { A "Defender: $_" }
try {
  Get-WinEvent -LogName 'Microsoft-Windows-CodeIntegrity/Operational' -MaxEvents 30 -ErrorAction Stop |
    Where-Object { $_.Message -match 'Watchdog' } | Select-Object -First 3 | ForEach-Object { A "CodeIntegrity [$($_.TimeCreated)]: $($_.Message)" }
} catch { A "CodeIntegrity: $_" }
$r | Set-Content -Path $out -Encoding UTF8
notepad $out
