# SPIKE (dialog focus after a drop): logs foreground-window changes, process name and window class only (no titles).
param([int]$Seconds = 40, [string]$Out, [switch]$SendEsc)
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class Fg {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize; public int flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rc; }
  [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
}
'@
$sw = [Diagnostics.Stopwatch]::StartNew()
$last = ''
$lines = New-Object System.Collections.Generic.List[string]
$sent = $false
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $h = [Fg]::GetForegroundWindow()
    $procId = 0; $tid = [Fg]::GetWindowThreadProcessId($h, [ref]$procId)
    $name = try { (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { '?' }
    $info = New-Object Fg+GUITHREADINFO; $info.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($info)
    $focus = if ([Fg]::GetGUIThreadInfo($tid, [ref]$info) -and $info.hwndFocus -ne [IntPtr]::Zero) { [Fg]::Cls($info.hwndFocus) } else { '-' }
    $now = "$name | $([Fg]::Cls($h)) | focus=$focus"
    if ($SendEsc -and -not $sent -and $name -eq 'WinTools.Dialogs') {
        Start-Sleep -Milliseconds 400
        [Fg]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero); [Fg]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
        $sent = $true; $lines.Add(('{0,7:N1}s  ESC sent while WinTools.Dialogs was in front' -f $sw.Elapsed.TotalSeconds))
    }
    if ($now -ne $last) { $lines.Add(('{0,7:N1}s  {1}' -f $sw.Elapsed.TotalSeconds, $now)); $last = $now }
    Start-Sleep -Milliseconds 100
}
$lines | Set-Content -LiteralPath $Out -Encoding UTF8
