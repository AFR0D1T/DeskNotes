# Measures how long DeskNotes takes from process start until its window is on screen.
# Waits until the sticker isn't the active window, so it never interrupts typing.
param([int]$Runs = 3, [string]$Exe = "$env:LOCALAPPDATA\Programs\DeskNotes\DeskNotes.exe")

Add-Type -Name WM -Namespace DN -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
public static bool HasVisibleWindow(uint pid) {
    bool found = false;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { found = true; return false; } return true; }, IntPtr.Zero);
    return found;
}
'@

function WaitIdle {
    $p = Get-Process DeskNotes -ErrorAction SilentlyContinue
    if (-not $p) { return }
    for ($i = 0; $i -lt 60; $i++) {
        $fp = 0; [DN.WM]::GetWindowThreadProcessId([DN.WM]::GetForegroundWindow(), [ref]$fp) | Out-Null
        if ($fp -ne $p.Id) { break }; Start-Sleep 2
    }
    Start-Sleep 1
}

$times = @()
for ($r = 0; $r -lt $Runs; $r++) {
    WaitIdle
    Get-Process DeskNotes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 2
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $Exe -PassThru
    while ($sw.ElapsedMilliseconds -lt 15000 -and -not [DN.WM]::HasVisibleWindow([uint32]$p.Id)) { Start-Sleep -Milliseconds 5 }
    $times += $sw.ElapsedMilliseconds
    Start-Sleep 3
}
"startup ms: " + ($times -join ", ") + "   (best " + ($times | Measure-Object -Minimum).Minimum + ")"
