# ============================================================
#  ClubPortableLinker - nightly auto-test (daily via Task Scheduler)
#  Non-destructive: build from source + read-only package/junction checks.
#  Lives in %ProgramData% so it does NOT depend on any game disk (D:/E:).
#  Drive-agnostic: finds packages on ALL fixed drives.
#  ASCII-only (Windows PowerShell 5.1 reads .ps1 as ANSI -> avoid Cyrillic).
#  Linker exe is a WinExe: run via Start-Process -Wait (PS won't wait on "&").
# ============================================================
$ErrorActionPreference = "Continue"

$repo     = (Get-ChildItem "C:\Users\*\Desktop\Work template\01_Source\Symbolics\ClubPortableLinker" -Directory -EA SilentlyContinue | Select-Object -First 1).FullName
$exe      = (Get-ChildItem "C:\Users\*\Desktop\Work template\02_Ready_Apps\ClubPortableLinker\ClubPortableLinker.exe" -EA SilentlyContinue | Select-Object -First 1).FullName
$repDir   = Join-Path $PSScriptRoot "reports"
New-Item -ItemType Directory -Force -Path $repDir | Out-Null
$stamp    = Get-Date -Format "yyyy-MM-dd_HHmm"
$report   = Join-Path $repDir "$stamp.log"
$status   = Join-Path $PSScriptRoot "LAST_STATUS.txt"
$fail     = 0

function Log($m){ $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $m; Add-Content -Path $report -Value $line -Encoding UTF8; Write-Host $line }

function Run-Exe($argList){
  $o = Join-Path $env:TEMP ("cpl_" + [guid]::NewGuid().ToString("N") + ".txt")
  try {
    $p = Start-Process -FilePath $exe -ArgumentList $argList -Wait -PassThru -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError ($o + ".err")
    $txt = (Get-Content $o -Raw -EA SilentlyContinue)
    Remove-Item $o,($o+".err") -Force -EA SilentlyContinue
    return @{ Code = $p.ExitCode; Out = $txt }
  } catch { return @{ Code = -1; Out = $_.Exception.Message } }
}

Log "=== ClubPortableLinker nightly $stamp ==="
Log ("repo=" + $repo)
Log ("exe=" + $exe)

# 1) Drives
foreach($d in (Get-PSDrive -PSProvider FileSystem)){ if($d.Free -ne $null){ Log ("drive {0}: free {1} GB" -f $d.Name, [math]::Round($d.Free/1GB,1)) } }

# 2) Build from source (catch regressions)
if($repo -and (Test-Path $repo)){
  Push-Location $repo
  & dotnet build -c Release --nologo 2>&1 | Out-Null
  if($LASTEXITCODE -eq 0){ Log "BUILD: OK" } else { Log ("BUILD: FAIL (" + $LASTEXITCODE + ")"); $fail++ }
  Pop-Location
} else { Log "BUILD: repo not found"; $fail++ }

# 3) Find packages on ANY drive + verify each.
$searchRoots = New-Object System.Collections.Generic.List[string]
foreach($drv in ([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady })){
  $searchRoots.Add((Join-Path $drv.Name "Programs"))
  $searchRoots.Add((Join-Path $drv.Name "Portable"))
  $searchRoots.Add((Join-Path $drv.Name "ClubPortable"))
}
$settingsFile = if($exe){ Join-Path (Split-Path $exe) "linker.settings.json" } else { $null }
if($settingsFile -and (Test-Path $settingsFile)){
  try { (Get-Content $settingsFile -Raw | ConvertFrom-Json).CatalogRoots | ForEach-Object { if($_){ $searchRoots.Add([string]$_) } } } catch {}
}
$pkgPaths = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
foreach($root in ($searchRoots | Select-Object -Unique)){
  if(Test-Path $root){
    Get-ChildItem $root -Directory -Recurse -Depth 2 -EA SilentlyContinue |
      Where-Object { Test-Path (Join-Path $_.FullName ".portable\manifest.json") } |
      ForEach-Object { [void]$pkgPaths.Add($_.FullName) }
  }
}
$pkgs = $pkgPaths | ForEach-Object { Get-Item $_ }
Log ("Search roots: " + (($searchRoots | Select-Object -Unique) -join "; "))
Log ("Packages found (any drive): " + ($pkgs | Measure-Object).Count)
foreach($p in $pkgs){
  $bad = 0
  if($exe){ $r = Run-Exe @('--verify-package','--package',$p.FullName); if($r.Code -ne 0){ $bad++; Log ("  verify exit=" + $r.Code) } }
  else { Log "  exe not found"; $bad++ }
  try { $null = Get-Content (Join-Path $p.FullName ".portable\manifest.json") -Raw | ConvertFrom-Json }
  catch { $bad++; Log ("  manifest read error: " + $_.Exception.Message) }
  if($bad -eq 0){ Log ("verify " + $p.Name + ": OK") } else { Log ("verify " + $p.Name + ": PROBLEMS (" + $bad + ")"); $fail++ }
}

# 4) Platform catalog: which known launchers are installed (info)
$known = @{
  "Steam"="C:\Program Files (x86)\Steam"; "Epic"="C:\Program Files\Epic Games";
  "Ubisoft"="C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher"; "RAGEMP"="C:\RAGEMP";
  "RSI"="C:\Program Files\Roberts Space Industries"; "BlueStacks"="C:\Program Files\BlueStacks_nxt"
}
$present = ($known.GetEnumerator() | Where-Object { Test-Path $_.Value } | ForEach-Object { $_.Key }) -join ", "
Log ("platforms present: " + $present)

# 5) Trim history (keep 30)
Get-ChildItem $repDir -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -Skip 30 | Remove-Item -Force -EA SilentlyContinue

# 6) Status signal (for quick glance / external monitoring) + alert on problems
$verdict = if($fail -eq 0){ "ALL OK" } else { "PROBLEMS - $fail" }
Set-Content -Path $status -Value ("{0}  {1}  (packages: {2})" -f (Get-Date -Format "yyyy-MM-dd HH:mm"), $verdict, ($pkgs | Measure-Object).Count) -Encoding UTF8
if($fail -gt 0){
  Add-Content -Path (Join-Path $PSScriptRoot "ALERTS.log") -Value ("[{0}] {1} -> {2}" -f $stamp, $verdict, $report) -Encoding UTF8
  try {
    [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime]
    $t=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
    $t.GetElementsByTagName('text')[0].AppendChild($t.CreateTextNode('ClubPortableLinker nightly')) | Out-Null
    $t.GetElementsByTagName('text')[1].AppendChild($t.CreateTextNode($verdict)) | Out-Null
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('ClubPortableLinker').Show([Windows.UI.Notifications.ToastNotification]::new($t))
  } catch {}
}

Log ("RESULT: " + $verdict)
Log "=== end ==="
exit $fail
