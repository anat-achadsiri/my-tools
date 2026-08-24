using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace SoftSnapWPF
{
    public partial class PreviewWindow : Window
    {
        private readonly string _filepath;
        public event Action? FileDeleted;
        public event Action? FileSaved;   // ไฟล์ถูกเขียนทับ (Save ในตัว หรือบันทึกจาก MS Paint)
        public event Action? PathCopied;  // กดปุ่ม Copy Path ในหน้า Preview

        // ICommand for ESC / Ctrl+Z KeyBindings
        public ICommand CloseCommand { get; }
        public ICommand UndoCommand { get; }

        // ── Annotation state ────────────────────────────────────────
        private enum Tool { None, Pen, Rect, Arrow, Text }

        private Tool _tool = Tool.None;
        private Color _color = Color.FromRgb(0xFF, 0x3B, 0x30);
        private double _baseThickness = 4;
        private double _scaleFactor = 1.0;   // scales stroke to image resolution

        private bool _drawing;
        private Point _start;
        private Polyline? _curPolyline;
        private Rectangle? _curRect;
        private System.Windows.Shapes.Path? _curArrow;
        private TextBox? _activeTextBox;

        private readonly System.Collections.Generic.List<UIElement> _undoStack = new();

        // ── Reload-on-external-edit (MS Paint ฯลฯ) ──────────────────
        private FileSystemWatcher? _watcher;
        private DispatcherTimer? _reloadDebounce;

        private double Thickness => _baseThickness * _scaleFactor;
        private bool HasAnnotations => _undoStack.Count > 0 || _activeTextBox != null;

        public PreviewWindow(string filepath, bool isDark)
        {
            CloseCommand = new RelayCommand(_ =>
            {
                if (_activeTextBox != null) { CancelActiveText(); return; }
                Close();
            });
            UndoCommand = new RelayCommand(_ => Undo());

            InitializeComponent();
            _filepath = filepath;

            // Theme
            PreviewWin.Background = new SolidColorBrush(isDark
                ? Color.FromRgb(12, 10, 26)
                : Color.FromRgb(242, 242, 247));

            FileNameLabel.Text = Path.GetFileName(filepath);
            FullPathLabel.Text = filepath;

            if (LoadImage())
            {
                // Size window to image
                var bi = (BitmapSource)PreviewImage.Source;
                var sw = SystemParameters.PrimaryScreenWidth * 0.85;
                var sh = SystemParameters.PrimaryScreenHeight * 0.85;
                var ratio = Math.Min(sw / bi.PixelWidth, sh / bi.PixelHeight);
                ratio = Math.Min(ratio, 1.0);

                Width = bi.PixelWidth * ratio + 40;
                Height = bi.PixelHeight * ratio + 160;
            }

            StartWatcher();
            Closed += (_, _) => StopWatcher();
        }

        // ── Image loading / reload ──────────────────────────────────
        private bool LoadImage()
        {
            try
            {
                // Decode from memory so the file is never locked
                byte[] bytes = File.ReadAllBytes(_filepath);
                var bi = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                }
                bi.Freeze();

                PreviewImage.Source = bi;
                EditorRoot.Width = bi.PixelWidth;
                EditorRoot.Height = bi.PixelHeight;
                _scaleFactor = Math.Max(1.0, bi.PixelWidth / 1200.0);
                return true;
            }
            catch (Exception ex)
            {
                PreviewImage.Visibility = Visibility.Collapsed;
                FileNameLabel.Text = $"Error: {ex.Message}";
                return false;
            }
        }

        private void ReloadImageWithRetry()
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(_filepath);
                    var bi = new BitmapImage();
                    using (var ms = new MemoryStream(bytes))
                    {
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = ms;
                        bi.EndInit();
                    }
                    bi.Freeze();

                    PreviewImage.Source = bi;
                    PreviewImage.Visibility = Visibility.Visible;
                    EditorRoot.Width = bi.PixelWidth;
                    EditorRoot.Height = bi.PixelHeight;
                    _scaleFactor = Math.Max(1.0, bi.PixelWidth / 1200.0);
                    FileSaved?.Invoke();
                    return;
                }
                catch (IOException)
                {
                    // file still being written — wait and retry
                    System.Threading.Thread.Sleep(250);
                }
                catch { return; }
            }
        }

        private void StartWatcher()
        {
            try
            {
                var dir = Path.GetDirectoryName(_filepath);
                if (string.IsNullOrEmpty(dir)) return;

                _watcher = new FileSystemWatcher(dir, Path.GetFileName(_filepath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Changed += (_, _) => Dispatcher.BeginInvoke(ScheduleReload);
                _watcher.EnableRaisingEvents = true;

                _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _reloadDebounce.Tick += (_, _) =>
                {
                    _reloadDebounce!.Stop();
                    ReloadImageWithRetry();
                };
            }
            catch { /* watcher is best-effort */ }
        }

        private void ScheduleReload()
        {
            _reloadDebounce?.Stop();
            _reloadDebounce?.Start();
        }

        private void StopWatcher()
        {
            _reloadDebounce?.Stop();
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        // ── Toolbar handlers ────────────────────────────────────────
        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;
            _tool = Enum.TryParse<Tool>(tag, out var t) ? t : Tool.None;

            if (AnnotationCanvas == null) return;
            AnnotationCanvas.Cursor = _tool switch
            {
                Tool.None => Cursors.Arrow,
                Tool.Text => Cursors.IBeam,
                _ => Cursors.Cross
            };
        }

        private void Color_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string hex)
                _color = (Color)ColorConverter.ConvertFromString(hex);
        }

        private void Size_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string s &&
                double.TryParse(s, out var v))
                _baseThickness = v;
        }

        private void UndoBtn_Click(object sender, RoutedEventArgs e) => Undo();

        private void Undo()
        {
            if (_activeTextBox != null) { CancelActiveText(); return; }
            if (_undoStack.Count == 0) return;

            var last = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            AnnotationCanvas.Children.Remove(last);
        }

        // ── Drawing ─────────────────────────────────────────────────
        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CommitActiveText();
            if (_tool == Tool.None) return;

            _start = e.GetPosition(AnnotationCanvas);

            if (_tool == Tool.Text)
            {
                BeginTextInput(_start);
                e.Handled = true;
                return;
            }

            _drawing = true;
            AnnotationCanvas.CaptureMouse();
            var brush = new SolidColorBrush(_color);

            switch (_tool)
            {
                case Tool.Pen:
                    _curPolyline = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = Thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    _curPolyline.Points.Add(_start);
                    AnnotationCanvas.Children.Add(_curPolyline);
                    break;

                case Tool.Rect:
                    _curRect = new Rectangle
                    {
                        Stroke = brush,
                        StrokeThickness = Thickness,
                        RadiusX = Thickness / 2,
                        RadiusY = Thickness / 2
                    };
                    Canvas.SetLeft(_curRect, _start.X);
                    Canvas.SetTop(_curRect, _start.Y);
                    AnnotationCanvas.Children.Add(_curRect);
                    break;

                case Tool.Arrow:
                    _curArrow = new System.Windows.Shapes.Path
                    {
                        Stroke = brush,
                        StrokeThickness = Thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    AnnotationCanvas.Children.Add(_curArrow);
                    break;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_drawing) return;
            var pos = e.GetPosition(AnnotationCanvas);

            if (_curPolyline != null)
            {
                _curPolyline.Points.Add(pos);
            }
            else if (_curRect != null)
            {
                Canvas.SetLeft(_curRect, Math.Min(_start.X, pos.X));
                Canvas.SetTop(_curRect, Math.Min(_start.Y, pos.Y));
                _curRect.Width = Math.Abs(pos.X - _start.X);
                _curRect.Height = Math.Abs(pos.Y - _start.Y);
            }
            else if (_curArrow != null)
            {
                _curArrow.Data = BuildArrowGeometry(_start, pos);
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_drawing) return;
            _drawing = false;
            AnnotationCanvas.ReleaseMouseCapture();

            UIElement? finished = (UIElement?)_curPolyline ?? (UIElement?)_curRect ?? _curArrow;
            _curPolyline = null;
            _curRect = null;
            _curArrow = null;

            if (finished == null) return;

            // discard zero-size accidental clicks (except pen dots)
            var pos = e.GetPosition(AnnotationCanvas);
            bool tooSmall = finished is not Polyline &&
                            Math.Abs(pos.X - _start.X) < 2 && Math.Abs(pos.Y - _start.Y) < 2;
            if (tooSmall)
                AnnotationCanvas.Children.Remove(finished);
            else
                _undoStack.Add(finished);
        }

        private static Geometry BuildArrowGeometry(Point s, Point e)
        {
            var g = new GeometryGroup();
            g.Children.Add(new LineGeometry(s, e));

            var d = e - s;
            if (d.Length > 1)
            {
                d.Normalize();
                double headLen = 14;
                double angle = Math.PI / 7;

                var back = new Vector(-d.X, -d.Y);
                var left = RotateVector(back, angle) * headLen;
                var right = RotateVector(back, -angle) * headLen;

                g.Children.Add(new LineGeometry(e, e + left));
                g.Children.Add(new LineGeometry(e, e + right));
            }
            return g;
        }

        private static Vector RotateVector(Vector v, double rad)
        {
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }

        // ── Text tool ───────────────────────────────────────────────
        private void BeginTextInput(Point pos)
        {
            double fontSize = Math.Max(14, Thickness * 6);
            var tb = new TextBox
            {
                MinWidth = fontSize * 3,
                FontFamily = new FontFamily("Kanit, Segoe UI"),
                FontSize = fontSize,
                Foreground = new SolidColorBrush(_color),
                Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                BorderBrush = new SolidColorBrush(_color),
                BorderThickness = new System.Windows.Thickness(1),
                Padding = new System.Windows.Thickness(2),
                AcceptsReturn = false
            };
            Canvas.SetLeft(tb, pos.X);
            Canvas.SetTop(tb, pos.Y);

            tb.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter) { CommitActiveText(); ke.Handled = true; }
                else if (ke.Key == Key.Escape) { CancelActiveText(); ke.Handled = true; }
            };
            tb.LostFocus += (_, _) => CommitActiveText();

            AnnotationCanvas.Children.Add(tb);
            _activeTextBox = tb;
            Dispatcher.BeginInvoke(() => tb.Focus(), DispatcherPriority.Input);
        }

        private void CommitActiveText()
        {
            var tb = _activeTextBox;
            if (tb == null) return;
            _activeTextBox = null;

            double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
            string text = tb.Text;
            AnnotationCanvas.Children.Remove(tb);

            if (string.IsNullOrWhiteSpace(text)) return;

            var block = new TextBlock
            {
                Text = text,
                FontFamily = tb.FontFamily,
                FontSize = tb.FontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = tb.Foreground
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y);
            AnnotationCanvas.Children.Add(block);
            _undoStack.Add(block);
        }

        private void CancelActiveText()
        {
            var tb = _activeTextBox;
            if (tb == null) return;
            _activeTextBox = null;
            AnnotationCanvas.Children.Remove(tb);
        }

        // ── Save (bake annotations into the original file) ──────────
        private void SaveBtn_Click(object sender, RoutedEventArgs e) => SaveAnnotations();

        private bool SaveAnnotations()
        {
            CommitActiveText();
            if (PreviewImage.Source is not BitmapSource src) return false;

            try
            {
                int w = src.PixelWidth, h = src.PixelHeight;

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(EditorRoot), null, new Rect(0, 0, w, h));

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);

                BitmapEncoder encoder = Path.GetExtension(_filepath).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
                    ".bmp" => new BmpBitmapEncoder(),
                    _ => new PngBitmapEncoder()
                };
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                using (var fs = new FileStream(_filepath, FileMode.Create, FileAccess.Write))
                    encoder.Save(fs);

                // annotations are now part of the file
                AnnotationCanvas.Children.Clear();
                _undoStack.Clear();
                ReloadImageWithRetry();

                FileNameLabel.Text = $"{Path.GetFileName(_filepath)}  ✓ บันทึกแล้ว";
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"บันทึกไม่สำเร็จ: {ex.Message}", "SoftSnap",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // ── Edit in MS Paint ────────────────────────────────────────
        private void EditInPaintBtn_Click(object sender, RoutedEventArgs e)
        {
            // bake any pending annotations first so Paint sees them
            if (HasAnnotations && !SaveAnnotations()) return;

            try
            {
                var p = Process.Start(new ProcessStartInfo("mspaint.exe", $"\"{_filepath}\"")
                {
                    UseShellExecute = true
                });

                // Reload when Paint exits (the FileSystemWatcher also reloads on every save)
                if (p != null)
                {
                    p.EnableRaisingEvents = true;
                    p.Exited += (_, _) => Dispatcher.BeginInvoke(ReloadImageWithRetry);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"เปิด MS Paint ไม่สำเร็จ: {ex.Message}", "SoftSnap",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Misc ────────────────────────────────────────────────────
        private void CopyPathBtn_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(_filepath);
            PathCopied?.Invoke();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            CommitActiveText();
            if (_undoStack.Count > 0)
            {
                var result = MessageBox.Show(this,
                    "มีการวาด/ข้อความที่ยังไม่ได้บันทึก ต้องการบันทึกก่อนปิดหรือไม่?",
                    "SoftSnap", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (result == MessageBoxResult.Yes && !SaveAnnotations()) { e.Cancel = true; return; }
            }
            base.OnClosing(e);
        }
    }

    // Simple ICommand implementation
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }
}
