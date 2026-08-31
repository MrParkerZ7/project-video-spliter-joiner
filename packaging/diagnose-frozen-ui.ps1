#Requires -Version 5.1
<#
.SYNOPSIS
    Capture evidence while the app's UI is unresponsive (T-138).

.DESCRIPTION
    Run this WHILE the app is misbehaving - before clicking anything to recover it, because the recovery
    click is what destroys the evidence. It records the things that distinguish the candidate causes of
    "the UI went dead until I clicked something":

      Responding / WM_NULL   is the UI thread pumping messages at all, or blocked?
      GetCapture             does some window hold mouse capture? (a Popup with StaysOpen="False" takes it)
      window list            is there an invisible layered window sitting over the app? (AllowsTransparency
                             + PopupAnimation can leave one behind)
      focus / foreground     did the app lose activation, so the first click only re-activates it?

    Read-only: it sends WM_NULL (a no-op probe) and reads window state. It clicks nothing and changes
    nothing, so it cannot itself clear the condition being measured.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging/diagnose-frozen-ui.ps1
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'VideoSplitJoiner.App',
    [string]$OutFile
)

Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class UiProbe {
  [DllImport("user32.dll")] public static extern IntPtr GetCapture();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetActiveWindow();
  [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr res);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);

  public static List<string> WindowsFor(uint pid) {
    var found = new List<string>();
    EnumProc cb = delegate(IntPtr h, IntPtr l) {
      uint q; GetWindowThreadProcessId(h, out q);
      if (q == pid) {
        var cn = new StringBuilder(256); GetClassName(h, cn, 256);
        var tt = new StringBuilder(256); GetWindowText(h, tt, 256);
        int exStyle = GetWindowLong(h, -20);            // GWL_EXSTYLE
        bool layered = (exStyle & 0x00080000) != 0;     // WS_EX_LAYERED
        bool transparent = (exStyle & 0x00000020) != 0; // WS_EX_TRANSPARENT
        found.Add(string.Format("hwnd={0} visible={1} enabled={2} layered={3} clickThrough={4} class={5} title='{6}'",
          h, IsWindowVisible(h), IsWindowEnabled(h), layered, transparent, cn, tt));
      }
      return true;
    };
    EnumWindows(cb, IntPtr.Zero);
    GC.KeepAlive(cb);
    return found;
  }
}
"@

$lines = New-Object System.Collections.Generic.List[string]
function Note([string]$s) { $lines.Add($s); Write-Output $s }

Note "=== VideoSplitJoiner UI freeze probe - $(Get-Date -Format o) ==="

$p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Note "app is not running (no window) - nothing to probe"; return }
$p.Refresh()

Note ""
Note "process     pid=$($p.Id)  mem=$([math]::Round($p.WorkingSet64/1MB))MB  started=$($p.StartTime)"
Note "Responding  $($p.Responding)   <- False means the UI thread is not pumping messages"

$res = [IntPtr]::Zero
$ok = [UiProbe]::SendMessageTimeout($p.MainWindowHandle, 0, [IntPtr]::Zero, [IntPtr]::Zero, 2, 3000, [ref]$res)
Note "WM_NULL     $(if ($ok -ne [IntPtr]::Zero) { 'answered within 3s (thread alive)' } else { 'TIMED OUT (thread blocked)' })"

Note ""
Note "capture     $([UiProbe]::GetCapture())   <- non-zero here means a window still holds the mouse"
Note "foreground  $([UiProbe]::GetForegroundWindow())"
Note "active      $([UiProbe]::GetActiveWindow())"
Note "app window  $($p.MainWindowHandle)"

Note ""
Note "windows owned by the app (a stray visible+layered one is the smoking gun):"
foreach ($w in [UiProbe]::WindowsFor([uint32]$p.Id)) { Note "  $w" }

Note ""
Note "READ THIS AS:"
Note "  Responding=False / WM_NULL timeout .. the UI thread is stuck (deadlock or a blocking call), not a popup"
Note "  capture != 0 ....................... a window still holds mouse capture - input is being swallowed"
Note "  extra visible layered window ....... an un-dismissed popup is sitting over the app"
Note "  foreground != app window ........... the app merely lost activation; the first click re-activates it"

if (-not $OutFile) {
    $OutFile = Join-Path $env:TEMP ("vsj-freeze-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
}
$lines | Out-File -FilePath $OutFile -Encoding utf8
Write-Output ""
Write-Output "saved -> $OutFile"
