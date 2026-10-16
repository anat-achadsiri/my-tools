<#
  MS Teams Auto-away - โปรแกรมกันสถานะ Teams เปลี่ยนเป็น Away
  ทำงานเป็นไอคอนใน System Tray (มุมขวาล่าง)
    - ดับเบิลคลิกไอคอน = เปิด/ปิด
    - คลิกขวา = เมนู (เปิด/ปิด, ตั้งเวลา, เริ่มพร้อม Windows, ออก)
  เปิดโปรแกรมด้วย AutoAway.vbs (ไม่มีหน้าต่าง console)
#>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class Jiggler {
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; public uint pad1; public uint pad2; }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint esFlags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);

    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    public static double IdleSeconds() {
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);
        GetLastInputInfo(ref lii);
        return unchecked((uint)Environment.TickCount - lii.dwTime) / 1000.0;
    }

    static void Move(int dx, int dy) {
        INPUT[] inp = new INPUT[1];
        inp[0].type = INPUT_MOUSE;
        inp[0].mi.dx = dx; inp[0].mi.dy = dy; inp[0].mi.dwFlags = MOUSEEVENTF_MOVE;
        SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void Nudge() { Move(1, 0); System.Threading.Thread.Sleep(50); Move(-1, 0); }

    public static void KeepAwake(bool on) {
        SetThreadExecutionState(on ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED) : ES_CONTINUOUS);
    }
}
"@

# ---------- กันเปิดซ้ำ ----------
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\MSTeamsAutoAway', [ref]$createdNew)
if (-not $createdNew) {
    [System.Windows.Forms.MessageBox]::Show('โปรแกรมเปิดอยู่แล้ว ดูไอคอนที่มุมขวาล่าง (System Tray)', 'MS Teams Auto-away') | Out-Null
    exit
}

# ---------- ค่าตั้งต้น (จำไว้ในไฟล์ settings.json) ----------
$settingsPath = Join-Path $PSScriptRoot 'settings.json'
$settings = @{ Enabled = $true; IntervalSeconds = 60 }
if (Test-Path $settingsPath) {
    try {
        $saved = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $settings.Enabled = [bool]$saved.Enabled
        $settings.IntervalSeconds = [int]$saved.IntervalSeconds
    } catch { }
}
function Save-Settings {
    try { $settings | ConvertTo-Json | Set-Content $settingsPath -Encoding UTF8 } catch { }
}

# ---------- Startup shortcut ----------
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'MS Teams Auto-away.lnk'
$vbsPath = Join-Path $PSScriptRoot 'AutoAway.vbs'
function Set-Startup([bool]$on) {
    if ($on) {
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($startupLink)
        $lnk.TargetPath = 'wscript.exe'
        $lnk.Arguments = '"' + $vbsPath + '"'
        $lnk.WorkingDirectory = $PSScriptRoot
        $lnk.Save()
    } elseif (Test-Path $startupLink) {
        Remove-Item $startupLink -Force
    }
}

# ---------- ไอคอน (วงกลมเขียว = เปิด, เทา = ปิด) ----------
function New-StatusIcon([System.Drawing.Color]$color) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $color), 2, 2, 28, 28)
    $g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 3), 3, 3, 26, 26)
    $g.Dispose()
    $h = $bmp.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($h).Clone()
    [Jiggler]::DestroyIcon($h) | Out-Null
    $bmp.Dispose()
    return $icon
}
$iconOn  = New-StatusIcon ([System.Drawing.Color]::FromArgb(0x6B, 0xB7, 0x00))
$iconOff = New-StatusIcon ([System.Drawing.Color]::FromArgb(0x8A, 0x8A, 0x8A))

# ---------- Tray + เมนู ----------
$tray = New-Object System.Windows.Forms.NotifyIcon
$menu = New-Object System.Windows.Forms.ContextMenuStrip

$miToggle = $menu.Items.Add('เปิดใช้งาน')
$miToggle.Font = New-Object System.Drawing.Font($miToggle.Font, [System.Drawing.FontStyle]::Bold)
$menu.Items.Add('-') | Out-Null

$miInterval = New-Object System.Windows.Forms.ToolStripMenuItem 'ขยับเมาส์เมื่อไม่ได้ใช้เครื่อง'
$intervalItems = @()
foreach ($sec in 30, 60, 120, 240) {
    $label = if ($sec -lt 60) { "$sec วินาที" } else { "$($sec / 60) นาที" }
    $item = New-Object System.Windows.Forms.ToolStripMenuItem $label
    $item.Tag = $sec
    $item.Add_Click({
        param($s, $e)
        $settings.IntervalSeconds = [int]$s.Tag
        Save-Settings
        Update-UI
    })
    $miInterval.DropDownItems.Add($item) | Out-Null
    $intervalItems += $item
}
$menu.Items.Add($miInterval) | Out-Null

$miStartup = New-Object System.Windows.Forms.ToolStripMenuItem 'เริ่มพร้อม Windows'
$miStartup.Checked = Test-Path $startupLink
$miStartup.Add_Click({
    Set-Startup (-not $miStartup.Checked)
    $miStartup.Checked = Test-Path $startupLink
})
$menu.Items.Add($miStartup) | Out-Null

$menu.Items.Add('-') | Out-Null
$miExit = $menu.Items.Add('ออกจากโปรแกรม')

$tray.ContextMenuStrip = $menu

function Update-UI {
    if ($settings.Enabled) {
        $tray.Icon = $iconOn
        $tray.Text = "Teams Auto-away: เปิด (ขยับทุก $($settings.IntervalSeconds) วิ.)"
        $miToggle.Text = 'ปิดใช้งาน'
    } else {
        $tray.Icon = $iconOff
        $tray.Text = 'Teams Auto-away: ปิด'
        $miToggle.Text = 'เปิดใช้งาน'
    }
    foreach ($i in $intervalItems) { $i.Checked = ([int]$i.Tag -eq $settings.IntervalSeconds) }
    [Jiggler]::KeepAwake($settings.Enabled)
}

function Switch-Enabled {
    $settings.Enabled = -not $settings.Enabled
    Save-Settings
    Update-UI
    $msg = if ($settings.Enabled) { 'เปิดใช้งานแล้ว - สถานะ Teams จะไม่เปลี่ยนเป็น Away' } else { 'ปิดใช้งานแล้ว' }
    $tray.ShowBalloonTip(2000, 'MS Teams Auto-away', $msg, 'Info')
}

$miToggle.Add_Click({ Switch-Enabled })
$tray.Add_MouseDoubleClick({ Switch-Enabled })
$miExit.Add_Click({ [System.Windows.Forms.Application]::Exit() })

# ---------- ตัวจับเวลา ----------
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 5000
$timer.Add_Tick({
    if ($settings.Enabled -and [Jiggler]::IdleSeconds() -ge $settings.IntervalSeconds) {
        [Jiggler]::Nudge()
    }
})

Update-UI
$tray.Visible = $true
$timer.Start()
$tray.ShowBalloonTip(2000, 'MS Teams Auto-away', 'โปรแกรมทำงานอยู่ที่ System Tray (ดับเบิลคลิกไอคอนเพื่อเปิด/ปิด)', 'Info')

try {
    [System.Windows.Forms.Application]::Run()
}
finally {
    $timer.Stop()
    [Jiggler]::KeepAwake($false)
    $tray.Visible = $false
    $tray.Dispose()
    $mutex.ReleaseMutex()
}
