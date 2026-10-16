// MS Teams Auto-away - กันสถานะ Teams เปลี่ยนเป็น Away
// Build: build.bat  (ใช้ csc.exe ที่มากับ Windows)
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
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

    public static double IdleSeconds()
    {
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);
        GetLastInputInfo(ref lii);
        return unchecked((uint)Environment.TickCount - lii.dwTime) / 1000.0;
    }

    static void Move(int dx, int dy)
    {
        INPUT[] inp = new INPUT[1];
        inp[0].type = INPUT_MOUSE;
        inp[0].mi.dx = dx; inp[0].mi.dy = dy; inp[0].mi.dwFlags = MOUSEEVENTF_MOVE;
        SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void Nudge() { Move(1, 0); Thread.Sleep(50); Move(-1, 0); }

    public static void KeepAwake(bool on)
    {
        SetThreadExecutionState(on ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED) : ES_CONTINUOUS);
    }
}

// ค่าที่ตั้งไว้ เก็บใน Registry ของผู้ใช้ (exe ย้ายไปไหนก็ยังจำค่าได้)
static class Settings
{
    const string KeyPath = @"Software\MSTeamsAutoAway";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "MS Teams Auto-away";

    public static bool Enabled = true;
    public static int IntervalSeconds = 60;

    public static void Load()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
        {
            if (k == null) return;
            Enabled = Convert.ToInt32(k.GetValue("Enabled", 1)) != 0;
            IntervalSeconds = Convert.ToInt32(k.GetValue("IntervalSeconds", 60));
        }
    }

    public static void Save()
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            k.SetValue("Enabled", Enabled ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("IntervalSeconds", IntervalSeconds, RegistryValueKind.DWord);
        }
    }

    public static bool StartupEnabled
    {
        get
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                return k != null && k.GetValue(RunName) != null;
        }
        set
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunName, false);
            }
        }
    }
}

class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Icon iconOn = MakeIcon(Color.FromArgb(0x6B, 0xB7, 0x00));
    readonly Icon iconOff = MakeIcon(Color.FromArgb(0x8A, 0x8A, 0x8A));
    readonly ToolStripMenuItem miToggle = new ToolStripMenuItem();
    readonly ToolStripMenuItem miInterval = new ToolStripMenuItem("ขยับเมาส์เมื่อไม่ได้ใช้เครื่อง");
    readonly ToolStripMenuItem miStartup = new ToolStripMenuItem("เริ่มพร้อม Windows");

    public TrayApp()
    {
        Settings.Load();

        ContextMenuStrip menu = new ContextMenuStrip();
        miToggle.Font = new Font(menu.Font, FontStyle.Bold);
        miToggle.Click += delegate { Toggle(); };
        menu.Items.Add(miToggle);
        menu.Items.Add(new ToolStripSeparator());

        foreach (int sec in new[] { 30, 60, 120, 240 })
        {
            ToolStripMenuItem item = new ToolStripMenuItem(sec < 60 ? sec + " วินาที" : (sec / 60) + " นาที");
            item.Tag = sec;
            item.Click += (s, e) =>
            {
                Settings.IntervalSeconds = (int)((ToolStripMenuItem)s).Tag;
                Settings.Save();
                UpdateUI();
            };
            miInterval.DropDownItems.Add(item);
        }
        menu.Items.Add(miInterval);

        miStartup.Checked = Settings.StartupEnabled;
        miStartup.Click += delegate
        {
            Settings.StartupEnabled = !miStartup.Checked;
            miStartup.Checked = Settings.StartupEnabled;
        };
        menu.Items.Add(miStartup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ออกจากโปรแกรม", null, delegate { ExitThread(); });

        tray.ContextMenuStrip = menu;
        tray.MouseDoubleClick += delegate { Toggle(); };

        timer.Interval = 5000;
        timer.Tick += delegate
        {
            if (Settings.Enabled && Native.IdleSeconds() >= Settings.IntervalSeconds)
                Native.Nudge();
        };

        UpdateUI();
        tray.Visible = true;
        timer.Start();
        tray.ShowBalloonTip(2000, "MS Teams Auto-away",
            "โปรแกรมทำงานอยู่ที่ System Tray (ดับเบิลคลิกไอคอนเพื่อเปิด/ปิด)", ToolTipIcon.Info);
    }

    void Toggle()
    {
        Settings.Enabled = !Settings.Enabled;
        Settings.Save();
        UpdateUI();
        tray.ShowBalloonTip(2000, "MS Teams Auto-away",
            Settings.Enabled ? "เปิดใช้งานแล้ว - สถานะ Teams จะไม่เปลี่ยนเป็น Away" : "ปิดใช้งานแล้ว",
            ToolTipIcon.Info);
    }

    void UpdateUI()
    {
        bool on = Settings.Enabled;
        tray.Icon = on ? iconOn : iconOff;
        tray.Text = on ? "Teams Auto-away: เปิด (ขยับทุก " + Settings.IntervalSeconds + " วิ.)" : "Teams Auto-away: ปิด";
        miToggle.Text = on ? "ปิดใช้งาน" : "เปิดใช้งาน";
        foreach (ToolStripMenuItem i in miInterval.DropDownItems)
            i.Checked = (int)i.Tag == Settings.IntervalSeconds;
        Native.KeepAwake(on);
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        Native.KeepAwake(false);
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }

    // ไอคอนวงกลม (เขียว = เปิด, เทา = ปิด)
    public static Bitmap DrawCircle(Color color, int size)
    {
        Bitmap bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float pad = size / 16f, pen = size / 10f;
            using (Brush b = new SolidBrush(color))
                g.FillEllipse(b, pad, pad, size - 2 * pad, size - 2 * pad);
            using (Pen p = new Pen(Color.White, pen))
                g.DrawEllipse(p, pad + pen / 2, pad + pen / 2, size - 2 * pad - pen, size - 2 * pad - pen);
        }
        return bmp;
    }

    static Icon MakeIcon(Color color)
    {
        using (Bitmap bmp = DrawCircle(color, 32))
        {
            IntPtr h = bmp.GetHicon();
            Icon icon = (Icon)Icon.FromHandle(h).Clone();
            Native.DestroyIcon(h);
            return icon;
        }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool createdNew;
        using (Mutex mutex = new Mutex(true, @"Local\MSTeamsAutoAway.exe", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show("โปรแกรมเปิดอยู่แล้ว ดูไอคอนที่มุมขวาล่าง (System Tray)", "MS Teams Auto-away");
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }
}
