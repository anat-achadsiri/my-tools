using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SoftSnapWPF
{
    public partial class RegionSelectWindow : Window
    {
        private readonly BitmapSource _fullImage;
        private Point _startPoint;
        private bool _isDragging;

        public Int32Rect? SelectedRect { get; private set; }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public RegionSelectWindow(BitmapSource fullImage)
        {
            InitializeComponent();
            _fullImage = fullImage;

            Loaded += (_, _) =>
            {
                BgImage.Source = _fullImage;

                // Hide 4-edge dim rects initially (FullDim covers all)
                DimTop.Visibility = Visibility.Collapsed;
                DimBottom.Visibility = Visibility.Collapsed;
                DimLeft.Visibility = Visibility.Collapsed;
                DimRight.Visibility = Visibility.Collapsed;

                // Force keyboard focus via Win32
                var hwnd = new WindowInteropHelper(this).Handle;
                SetForegroundWindow(hwnd);
                Activate();
                Focus();
                Keyboard.Focus(this);
            };
        }

        // ESC handling — override at the deepest level
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelAndClose();
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelAndClose();
            }
        }

        private void CancelAndClose()
        {
            _isDragging = false;
            RootGrid.ReleaseMouseCapture();
            DialogResult = false;
            Close();
        }

        // Right-click to cancel too
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            e.Handled = true;
            CancelAndClose();
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(RootGrid);
            _isDragging = true;

            // Hide full dim + hint, show 4-edge dim
            FullDim.Visibility = Visibility.Collapsed;
            HintBorder.Visibility = Visibility.Collapsed;
            DimTop.Visibility = Visibility.Visible;
            DimBottom.Visibility = Visibility.Visible;
            DimLeft.Visibility = Visibility.Visible;
            DimRight.Visibility = Visibility.Visible;

            SelectionBorder.Visibility = Visibility.Visible;
            SizeLabel.Visibility = Visibility.Visible;

            UpdateSelection(_startPoint);
            RootGrid.CaptureMouse();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            UpdateSelection(e.GetPosition(RootGrid));
        }

        private void UpdateSelection(Point pos)
        {
            var sw = RootGrid.ActualWidth;
            var sh = RootGrid.ActualHeight;

            var x = Math.Min(pos.X, _startPoint.X);
            var y = Math.Min(pos.Y, _startPoint.Y);
            var w = Math.Abs(pos.X - _startPoint.X);
            var h = Math.Abs(pos.Y - _startPoint.Y);

            // Clamp to window
            x = Math.Max(0, x);
            y = Math.Max(0, y);
            if (x + w > sw) w = sw - x;
            if (y + h > sh) h = sh - y;

            // Position selection border
            SelectionBorder.Margin = new Thickness(x, y, sw - x - w, sh - y - h);

            // Position 4 dim rectangles around selection (cutout effect)
            DimTop.Margin = new Thickness(0, 0, 0, sh - y);
            DimBottom.Margin = new Thickness(0, y + h, 0, 0);
            DimLeft.Margin = new Thickness(0, y, sw - x, sh - y - h);
            DimRight.Margin = new Thickness(x + w, y, 0, sh - y - h);

            // Size label (pixel dimensions)
            var iw = _fullImage.PixelWidth;
            var ih = _fullImage.PixelHeight;
            var rx = iw / sw;
            var ry = ih / sh;
            var pw = (int)(w * rx);
            var ph = (int)(h * ry);
            SizeText.Text = $"{pw} x {ph} px";

            // Position size label near selection
            var labelY = y > 30 ? y - 28 : y + h + 4;
            SizeLabel.Margin = new Thickness(x, labelY, 0, 0);
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            RootGrid.ReleaseMouseCapture();

            var pos = e.GetPosition(RootGrid);
            var sw = RootGrid.ActualWidth;
            var sh = RootGrid.ActualHeight;
            var iw = _fullImage.PixelWidth;
            var ih = _fullImage.PixelHeight;
            var rx = iw / sw;
            var ry = ih / sh;

            var x0 = (int)(Math.Min(_startPoint.X, pos.X) * rx);
            var y0 = (int)(Math.Min(_startPoint.Y, pos.Y) * ry);
            var x1 = (int)(Math.Max(_startPoint.X, pos.X) * rx);
            var y1 = (int)(Math.Max(_startPoint.Y, pos.Y) * ry);

            // Clamp to image bounds
            x0 = Math.Max(0, Math.Min(x0, iw));
            y0 = Math.Max(0, Math.Min(y0, ih));
            x1 = Math.Max(0, Math.Min(x1, iw));
            y1 = Math.Max(0, Math.Min(y1, ih));

            var w = x1 - x0;
            var h = y1 - y0;

            if (w > 10 && h > 10)
            {
                SelectedRect = new Int32Rect(x0, y0, w, h);
                DialogResult = true;
            }
            else
            {
                DialogResult = false;
            }
            Close();
        }
    }
}
