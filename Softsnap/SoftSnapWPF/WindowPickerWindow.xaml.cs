using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoftSnapWPF
{
    public partial class WindowPickerWindow : Window
    {
        public IntPtr SelectedHwnd { get; private set; }
        public string SelectedTitle { get; private set; } = "";

        private readonly IntPtr _ownHwnd;
        private readonly List<WindowInfo> _windows = new();

        public WindowPickerWindow(IntPtr ownerHwnd)
        {
            InitializeComponent();
            _ownHwnd = ownerHwnd;
            Loaded += (_, _) => LoadWindows();
        }

        private void LoadWindows()
        {
            _windows.Clear();
            WindowList.Items.Clear();

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (hwnd == _ownHwnd) return true;

                var title = GetWindowTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title)) return true;

                // Skip tiny/invisible windows
                GetWindowRect(hwnd, out RECT rect);
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w < 50 || h < 50) return true;

                // Skip certain system windows
                var className = GetWindowClassName(hwnd);
                if (className is "Progman" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                    or "Windows.UI.Core.CoreWindow")
                    return true;

                // Get process name
                string processName = "";
                try
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                }
                catch { }

                _windows.Add(new WindowInfo
                {
                    Hwnd = hwnd,
                    Title = title,
                    ProcessName = processName,
                    Width = w,
                    Height = h
                });

                return true;
            }, IntPtr.Zero);

            foreach (var wi in _windows)
            {
                var panel = new DockPanel { Margin = new Thickness(2) };

                // Thumbnail preview
                var thumb = CaptureWindowThumbnail(wi.Hwnd, 64, 48);
                if (thumb != null)
                {
                    var imgBorder = new Border
                    {
                        Width = 64,
                        Height = 48,
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.FromRgb(20, 20, 30)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 80)),
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(0, 0, 10, 0),
                        ClipToBounds = true,
                        Child = new Image
                        {
                            Source = thumb,
                            Stretch = Stretch.Uniform
                        }
                    };
                    DockPanel.SetDock(imgBorder, Dock.Left);
                    panel.Children.Add(imgBorder);
                }

                // Text info
                var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                textPanel.Children.Add(new TextBlock
                {
                    Text = wi.Title,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 350
                });
                textPanel.Children.Add(new TextBlock
                {
                    Text = $"{wi.ProcessName}  —  {wi.Width} x {wi.Height}",
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 160)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                panel.Children.Add(textPanel);

                WindowList.Items.Add(panel);
            }

            if (WindowList.Items.Count > 0)
                WindowList.SelectedIndex = 0;
        }

        private BitmapSource? CaptureWindowThumbnail(IntPtr hwnd, int maxW, int maxH)
        {
            try
            {
                GetWindowRect(hwnd, out RECT rect);
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w <= 0 || h <= 0) return null;

                using var bmp = new System.Drawing.Bitmap(w, h);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                var hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);

                // Scale down for thumbnail
                double scale = Math.Min((double)maxW / w, (double)maxH / h);
                int tw = Math.Max(1, (int)(w * scale));
                int th = Math.Max(1, (int)(h * scale));
                using var thumb = new System.Drawing.Bitmap(tw, th);
                using var tg = System.Drawing.Graphics.FromImage(thumb);
                tg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                tg.DrawImage(bmp, 0, 0, tw, th);

                var hBmp = thumb.GetHbitmap();
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                        hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally
                {
                    DeleteObject(hBmp);
                }
            }
            catch
            {
                return null;
            }
        }

        public BitmapSource? CaptureSelectedWindow()
        {
            if (SelectedHwnd == IntPtr.Zero) return null;

            try
            {
                GetWindowRect(SelectedHwnd, out RECT rect);
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w <= 0 || h <= 0) return null;

                using var bmp = new System.Drawing.Bitmap(w, h);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                var hdc = g.GetHdc();
                PrintWindow(SelectedHwnd, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);

                var hBmp = bmp.GetHbitmap();
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                        hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally
                {
                    DeleteObject(hBmp);
                }
            }
            catch
            {
                return null;
            }
        }

        private void CaptureBtn_Click(object sender, RoutedEventArgs e) => TryCapture();
        private void WindowList_DoubleClick(object sender, MouseButtonEventArgs e) => TryCapture();

        private void TryCapture()
        {
            var idx = WindowList.SelectedIndex;
            if (idx < 0 || idx >= _windows.Count) return;

            SelectedHwnd = _windows[idx].Hwnd;
            SelectedTitle = _windows[idx].Title;
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e) => LoadWindows();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            if (e.Key == Key.Enter) TryCapture();
        }

        // ── Win32 APIs ──────────────────────────────────────────────
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private class WindowInfo
        {
            public IntPtr Hwnd;
            public string Title = "";
            public string ProcessName = "";
            public int Width, Height;
        }
    }
}
