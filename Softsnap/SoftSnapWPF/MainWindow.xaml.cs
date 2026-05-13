using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoftSnapWPF
{
    public partial class MainWindow : Window
    {
        // ── Config ──────────────────────────────────────────────────
        private readonly string _appDir;
        private readonly string _configFile;
        private Dictionary<string, object?> _cfg = new();
        private string _saveDir = "";
        private string _currentAlbum = "Screenshots";
        private Dictionary<string, string> _albums = new();
        private readonly HashSet<string> _selected = new();
        private bool _isDark = true;
        private const int ThumbSize = 140;
        private const int Columns = 3;
        private int _galleryGeneration = 0; // cancel stale loads

        // ── Drag-select state ───────────────────────────────────────
        private bool _isDragging;
        private Point _dragStart;
        private readonly Dictionary<Border, string> _cardToFile = new();

        public MainWindow()
        {
            InitializeComponent();

            _appDir = AppDomain.CurrentDomain.BaseDirectory;
            _configFile = Path.Combine(_appDir, "softsnap_config.json");

            LoadConfig();
            InitAlbums();
            RefreshAlbumCombo();
            LoadGallery();

            // Close album popup when clicking outside
            PreviewMouseDown += (_, e) =>
            {
                if (!AlbumPopup.IsOpen) return;
                // Check if click is inside popup or overflow button
                var pos = e.GetPosition(AlbumPopupBorder);
                var inPopup = pos.X >= 0 && pos.Y >= 0
                    && pos.X <= AlbumPopupBorder.ActualWidth
                    && pos.Y <= AlbumPopupBorder.ActualHeight;
                var posBtn = e.GetPosition(AlbumOverflowBtn);
                var inBtn = posBtn.X >= 0 && posBtn.Y >= 0
                    && posBtn.X <= AlbumOverflowBtn.ActualWidth
                    && posBtn.Y <= AlbumOverflowBtn.ActualHeight;
                if (!inPopup && !inBtn)
                    AlbumPopup.IsOpen = false;
            };
        }

        // ── Config I/O ──────────────────────────────────────────────
        private void LoadConfig()
        {
            if (File.Exists(_configFile))
            {
                try
                {
                    var json = File.ReadAllText(_configFile);
                    _cfg = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
                }
                catch { _cfg = new(); }
            }

            _saveDir = GetCfgString("save_dir", Path.Combine(_appDir, "Screenshots"));
            _isDark = GetCfgString("theme", "dark") == "dark";
            _currentAlbum = GetCfgString("current_album", "Screenshots");

            ApplyTheme();
        }

        private string GetCfgString(string key, string def)
        {
            if (_cfg.TryGetValue(key, out var val) && val is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString() ?? def;
            if (val is string s) return s;
            return def;
        }

        private void SaveConfig()
        {
            _cfg["save_dir"] = _saveDir;
            _cfg["theme"] = _isDark ? "dark" : "light";
            _cfg["current_album"] = _currentAlbum;
            _cfg["albums"] = _albums;

            var json = JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFile, json);
        }

        // ── Albums ──────────────────────────────────────────────────
        private void InitAlbums()
        {
            if (_cfg.TryGetValue("albums", out var val) && val is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                _albums = new Dictionary<string, string>();
                foreach (var prop in je.EnumerateObject())
                    _albums[prop.Name] = prop.Value.GetString() ?? "";
            }

            if (_albums.Count == 0)
            {
                _albums["Screenshots"] = _saveDir;
                SaveConfig();
            }

            if (_albums.TryGetValue(_currentAlbum, out var dir))
                _saveDir = dir;

            Directory.CreateDirectory(_saveDir);
        }

        private void RefreshAlbumCombo()
        {
            AlbumCombo.SelectionChanged -= AlbumCombo_SelectionChanged;
            AlbumCombo.Items.Clear();
            foreach (var name in _albums.Keys)
                AlbumCombo.Items.Add(name);
            AlbumCombo.SelectedItem = _currentAlbum;
            AlbumCombo.SelectionChanged += AlbumCombo_SelectionChanged;

            PathLabel.Text = _saveDir;
            RefreshAlbumTabs();
        }

        private const int MaxVisibleTabs = 6;

        private void RefreshAlbumTabs()
        {
            AlbumTabs.Items.Clear();

            var albumNames = _albums.Keys.ToList();

            // Always show active tab first, then others
            var visible = new List<string>();
            if (albumNames.Contains(_currentAlbum))
                visible.Add(_currentAlbum);
            foreach (var n in albumNames)
                if (n != _currentAlbum && visible.Count < MaxVisibleTabs)
                    visible.Add(n);

            var overflow = albumNames.Where(n => !visible.Contains(n)).ToList();

            foreach (var name in visible)
                AlbumTabs.Items.Add(CreateAlbumPill(name));

            // Overflow dropdown
            if (overflow.Count > 0)
            {
                AlbumOverflowBtn.Visibility = Visibility.Visible;
                AlbumOverflowText.Text = $"\u25BE +{overflow.Count}";
                AlbumOverflowBtn.Background = new SolidColorBrush(_isDark
                    ? Color.FromRgb(45, 45, 50)
                    : Color.FromRgb(235, 235, 240));
                AlbumOverflowText.Foreground = new SolidColorBrush(_isDark
                    ? Color.FromRgb(180, 180, 190)
                    : Color.FromRgb(85, 85, 85));

                // Build popup items
                AlbumPopupItems.Items.Clear();
                foreach (var name in albumNames)
                {
                    var isActive = name == _currentAlbum;
                    var item = new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(12, 6, 12, 6),
                        Margin = new Thickness(2, 2, 2, 2),
                        Cursor = Cursors.Hand,
                        Background = new SolidColorBrush(isActive
                            ? (_isDark ? Color.FromRgb(55, 55, 75) : Color.FromRgb(0, 120, 215))
                            : Colors.Transparent)
                    };
                    var lbl = new TextBlock
                    {
                        Text = name,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 12,
                        FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = new SolidColorBrush(isActive
                            ? Colors.White
                            : (_isDark ? Color.FromRgb(220, 220, 220) : Color.FromRgb(30, 30, 30)))
                    };
                    item.Child = lbl;
                    var albumName = name;
                    item.MouseLeftButtonDown += (_, _) =>
                    {
                        AlbumPopup.IsOpen = false;
                        if (albumName != _currentAlbum)
                            AlbumCombo.SelectedItem = albumName;
                    };
                    item.MouseEnter += (s, _) =>
                    {
                        if (!_selected.Contains(albumName))
                            ((Border)s!).Background = new SolidColorBrush(_isDark
                                ? Color.FromRgb(50, 50, 55) : Color.FromRgb(240, 240, 245));
                    };
                    item.MouseLeave += (s, _) =>
                    {
                        var a = ((Border)s!).Child is TextBlock t && t.FontWeight == FontWeights.SemiBold;
                        ((Border)s).Background = new SolidColorBrush(a
                            ? (_isDark ? Color.FromRgb(55, 55, 75) : Color.FromRgb(0, 120, 215))
                            : Colors.Transparent);
                    };
                    AlbumPopupItems.Items.Add(item);
                }

                AlbumPopupBorder.Background = new SolidColorBrush(_isDark
                    ? Color.FromRgb(40, 40, 45) : Colors.White);
                AlbumPopupBorder.BorderBrush = new SolidColorBrush(_isDark
                    ? Color.FromRgb(60, 60, 65) : Color.FromRgb(224, 224, 224));
            }
            else
            {
                AlbumOverflowBtn.Visibility = Visibility.Collapsed;
            }
        }

        private Border CreateAlbumPill(string name)
        {
            var isActive = name == _currentAlbum;

            var pill = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(isActive
                    ? (_isDark ? Color.FromRgb(55, 55, 75) : Color.FromRgb(0, 120, 215))
                    : (_isDark ? Color.FromRgb(45, 45, 50) : Color.FromRgb(235, 235, 240))),
                BorderBrush = new SolidColorBrush(isActive
                    ? (_isDark ? Color.FromRgb(99, 102, 241) : Color.FromRgb(0, 120, 215))
                    : Colors.Transparent),
                BorderThickness = new Thickness(isActive ? 1 : 0)
            };

            var label = new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(isActive
                    ? (_isDark ? Color.FromRgb(230, 230, 255) : Colors.White)
                    : (_isDark ? Color.FromRgb(180, 180, 190) : Color.FromRgb(60, 60, 60)))
            };

            pill.Child = label;

            var albumName = name;
            pill.MouseLeftButtonDown += (_, _) =>
            {
                if (albumName == _currentAlbum) return;
                AlbumCombo.SelectedItem = albumName;
            };

            // Hover effect for inactive pills
            if (!isActive)
            {
                pill.MouseEnter += (s, _) => ((Border)s!).Background =
                    new SolidColorBrush(_isDark ? Color.FromRgb(55, 55, 60) : Color.FromRgb(220, 220, 228));
                pill.MouseLeave += (s, _) => ((Border)s!).Background =
                    new SolidColorBrush(_isDark ? Color.FromRgb(45, 45, 50) : Color.FromRgb(235, 235, 240));
            }

            return pill;
        }

        private void AlbumOverflowBtn_Click(object sender, MouseButtonEventArgs e)
        {
            AlbumPopup.IsOpen = !AlbumPopup.IsOpen;
        }

        private void AlbumCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AlbumCombo.SelectedItem is string name && _albums.TryGetValue(name, out var dir))
            {
                _currentAlbum = name;
                _saveDir = dir;
                Directory.CreateDirectory(_saveDir);
                SaveConfig();
                PathLabel.Text = _saveDir;
                _selected.Clear();
                RefreshAlbumTabs();
                LoadGallery();
                SetStatus($"Album: {name}");
            }
        }

        private void AddAlbumBtn_Click(object sender, RoutedEventArgs e)
        {
            // 1. กรอกชื่อ Album
            var nameDialog = new InputDialog("สร้าง Album ใหม่", "ชื่อ Album:", "");
            if (nameDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(nameDialog.Answer)) return;

            var name = nameDialog.Answer.Trim();

            // 2. เลือก Folder ปลายทาง
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = $"เลือก Folder ปลายทางที่จะสร้าง \"{name}\"",
                SelectedPath = _saveDir
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            // 3. สร้าง folder ชื่อ Album ใน Folder ปลายทาง
            var albumPath = Path.Combine(dlg.SelectedPath, name);
            Directory.CreateDirectory(albumPath);

            _albums[name] = albumPath;
            _currentAlbum = name;
            _saveDir = albumPath;
            SaveConfig();

            _selected.Clear();
            RefreshAlbumCombo();
            LoadGallery();
            SetStatus($"เพิ่ม Album: {name}");
        }

        private void BrowseAlbumBtn_Click(object sender, RoutedEventArgs e)
        {
            // เลือก Folder ที่มีอยู่แล้วเป็น Album
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "เลือก Folder ที่มีอยู่แล้วเป็น Album",
                SelectedPath = _saveDir
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var folderName = Path.GetFileName(dlg.SelectedPath) ?? "New Album";
            var nameDialog = new InputDialog("ตั้งชื่อ Album", "ชื่อ Album:", folderName);
            if (nameDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(nameDialog.Answer)) return;

            var name = nameDialog.Answer.Trim();
            _albums[name] = dlg.SelectedPath;
            _currentAlbum = name;
            _saveDir = dlg.SelectedPath;
            SaveConfig();

            _selected.Clear();
            RefreshAlbumCombo();
            LoadGallery();
            SetStatus($"เพิ่ม Album: {name}");
        }

        private void RemoveAlbumBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_albums.Count <= 1)
            {
                SetStatus("ต้องมีอย่างน้อย 1 Album", false);
                return;
            }
            var name = _currentAlbum;
            if (MessageBox.Show($"ลบ Album \"{name}\"?\n(ไม่ลบไฟล์จริง)", "ยืนยัน",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _albums.Remove(name);
            var first = _albums.Keys.First();
            _currentAlbum = first;
            _saveDir = _albums[first];
            SaveConfig();

            _selected.Clear();
            RefreshAlbumCombo();
            LoadGallery();
            SetStatus($"ลบ Album: {name}");
        }

        private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(_saveDir);
            Process.Start("explorer.exe", _saveDir);
        }

        // ── Gallery ─────────────────────────────────────────────────
        private void LoadGallery()
        {
            _galleryGeneration++;
            GalleryItems.Items.Clear();
            _cardToFile.Clear();

            if (!Directory.Exists(_saveDir)) return;

            var exts = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
            var files = Directory.GetFiles(_saveDir)
                .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            // Clean up invalid selections
            _selected.IntersectWith(files.Select(f => f));

            CountLabel.Text = $"{files.Count} รูป";

            if (files.Count == 0)
            {
                // Hide WrapPanel, show centered message directly
                GalleryItems.Visibility = Visibility.Collapsed;

                var empty = new TextBlock
                {
                    Text = "\U0001F4F7\nยังไม่มีภาพ\nกด F5 เพื่อ Capture",
                    Foreground = Brushes.Gray,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 18,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                // Add to the Grid parent of GalleryItems
                var parentGrid = (Grid)GalleryItems.Parent;
                if (parentGrid.Children.Count > 2)
                    parentGrid.Children.RemoveAt(2); // remove old empty message
                parentGrid.Children.Add(empty);
                return;
            }
            else
            {
                GalleryItems.Visibility = Visibility.Visible;
                var parentGrid = (Grid)GalleryItems.Parent;
                while (parentGrid.Children.Count > 2)
                    parentGrid.Children.RemoveAt(2);
            }

            // Create cards with placeholder thumbnails first (instant UI)
            var cardEntries = new List<(Border card, Image img, string filepath)>();
            foreach (var fp in files)
            {
                var (card, img) = CreateCard(fp);
                GalleryItems.Items.Add(card);
                cardEntries.Add((card, img, fp));
            }

            UpdateSelBar();

            // Load thumbnails async in background
            var gen = _galleryGeneration;
            _ = LoadThumbnailsAsync(cardEntries, gen);
        }

        private async Task LoadThumbnailsAsync(List<(Border card, Image img, string filepath)> entries, int generation)
        {
            const int batchSize = 8;

            for (int i = 0; i < entries.Count; i += batchSize)
            {
                if (_galleryGeneration != generation) return; // gallery was reloaded, stop

                var batch = entries.Skip(i).Take(batchSize).ToList();

                // Decode thumbnails on background thread
                var results = await Task.Run(() =>
                {
                    var decoded = new List<(int index, BitmapImage? bmp)>();
                    foreach (var (_, _, filepath) in batch)
                    {
                        if (_galleryGeneration != generation) return decoded;
                        try
                        {
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.UriSource = new Uri(filepath);
                            bi.DecodePixelWidth = ThumbSize;
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.EndInit();
                            bi.Freeze(); // allow cross-thread access
                            decoded.Add((0, bi));
                        }
                        catch
                        {
                            decoded.Add((0, null));
                        }
                    }
                    return decoded;
                });

                if (_galleryGeneration != generation) return;

                // Apply to UI on dispatcher thread
                for (int j = 0; j < results.Count && j < batch.Count; j++)
                {
                    var bmp = results[j].bmp;
                    var img = batch[j].img;
                    if (bmp != null)
                        img.Source = bmp;
                }
            }
        }

        private (Border card, Image img) CreateCard(string filepath)
        {
            var isSelected = _selected.Contains(filepath);
            var fname = Path.GetFileName(filepath);

            var textColor = _isDark
                ? new SolidColorBrush(Color.FromRgb(220, 220, 230))
                : new SolidColorBrush(Color.FromRgb(30, 30, 30));

            // Windows Explorer style: transparent bg, highlight on select
            var selectedBg = new SolidColorBrush(_isDark
                ? Color.FromArgb(60, 99, 102, 241)
                : Color.FromArgb(40, 0, 120, 215));
            var selectedBorder = new SolidColorBrush(_isDark
                ? Color.FromArgb(120, 99, 102, 241)
                : Color.FromArgb(100, 0, 120, 215));

            var card = new Border
            {
                Background = isSelected ? selectedBg : Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                BorderBrush = isSelected ? selectedBorder : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Margin = new Thickness(2),
                Width = 170,
                Cursor = Cursors.Hand
            };

            // Click = toggle select, Double-click = preview
            var fpClick = filepath;
            card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                    ShowPreview(fpClick);
                else
                    ToggleSelect(fpClick);
                e.Handled = true;
            };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            // Fixed-size thumbnail box (like Windows Explorer)
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var imgBorder = new Border
            {
                Width = ThumbSize,
                Height = ThumbSize,
                BorderBrush = new SolidColorBrush(_isDark
                    ? Color.FromRgb(60, 60, 80)
                    : Color.FromRgb(200, 200, 200)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                Child = img
            };
            stack.Children.Add(imgBorder);

            // Filename (centered, wrapping like Explorer)
            stack.Children.Add(new TextBlock
            {
                Text = fname,
                Foreground = textColor,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = ThumbSize + 10,
                MaxHeight = 36,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            card.Child = stack;
            _cardToFile[card] = filepath;
            return (card, img);
        }

        // ── Selection ───────────────────────────────────────────────
        private void ToggleSelect(string filepath)
        {
            if (_selected.Contains(filepath))
                _selected.Remove(filepath);
            else
                _selected.Add(filepath);

            UpdateSelBar();
            RefreshCardStyles();
        }

        private void SetSelected(string filepath, bool selected)
        {
            if (selected) _selected.Add(filepath);
            else _selected.Remove(filepath);
        }

        private void RefreshCardStyles()
        {
            var selectedBg = new SolidColorBrush(_isDark
                ? Color.FromArgb(60, 99, 102, 241)
                : Color.FromArgb(40, 0, 120, 215));
            var selectedBorder = new SolidColorBrush(_isDark
                ? Color.FromArgb(120, 99, 102, 241)
                : Color.FromArgb(100, 0, 120, 215));

            foreach (var item in GalleryItems.Items)
            {
                if (item is Border card && _cardToFile.TryGetValue(card, out var fp))
                {
                    var isSel = _selected.Contains(fp);
                    card.Background = isSel ? selectedBg : Brushes.Transparent;
                    card.BorderBrush = isSel ? selectedBorder : Brushes.Transparent;
                }
            }
        }

        // ── Drag-select (rubber-band) ───────────────────────────────
        private void GalleryScroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Only start drag on empty space (not on cards/buttons)
            if (e.OriginalSource is ScrollViewer || e.OriginalSource is Grid || e.OriginalSource is WrapPanel
                || e.OriginalSource is Border b && b == GalleryBorder)
            {
                _isDragging = true;
                _dragStart = e.GetPosition(GalleryItems);
                SelectionCanvas.IsHitTestVisible = false;
                SelectionRect.Visibility = Visibility.Collapsed;
                GalleryScroll.CaptureMouse();

                // Clear selection if not holding Ctrl
                if (Keyboard.Modifiers != ModifierKeys.Control)
                {
                    _selected.Clear();
                    RefreshCardStyles();
                    UpdateSelBar();
                }
            }
        }

        private void GalleryScroll_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;

            var pos = e.GetPosition(GalleryItems);
            var x = Math.Min(_dragStart.X, pos.X);
            var y = Math.Min(_dragStart.Y, pos.Y);
            var w = Math.Abs(_dragStart.X - pos.X);
            var h = Math.Abs(_dragStart.Y - pos.Y);

            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = w;
            SelectionRect.Height = h;
            SelectionRect.Visibility = Visibility.Visible;

            // Hit-test cards against rubber-band rect
            var selRect = new Rect(x, y, w, h);
            foreach (var item in GalleryItems.Items)
            {
                if (item is Border card && _cardToFile.TryGetValue(card, out var fp))
                {
                    var transform = card.TransformToAncestor(GalleryItems);
                    var cardRect = new Rect(transform.Transform(new Point(0, 0)),
                                            new Size(card.ActualWidth, card.ActualHeight));
                    SetSelected(fp, selRect.IntersectsWith(cardRect));
                }
            }
            RefreshCardStyles();
            UpdateSelBar();
        }

        private void GalleryScroll_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            SelectionRect.Visibility = Visibility.Collapsed;
            GalleryScroll.ReleaseMouseCapture();
        }

        private void SelectAllCheck_Click(object sender, RoutedEventArgs e)
        {
            if (SelectAllCheck.IsChecked == true)
            {
                foreach (var fp in _cardToFile.Values)
                    _selected.Add(fp);
            }
            else
            {
                _selected.Clear();
            }
            RefreshCardStyles();
            UpdateSelBar();
        }

        private void UpdateSelBar()
        {
            SelInfoLabel.Text = _selected.Count > 0 ? $"เลือกแล้ว {_selected.Count} รายการ" : "";
        }

        // ── Batch Actions ───────────────────────────────────────────
        private void CopySelBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selected.Count == 0) { SetStatus("ไม่ได้เลือกรูป", false); return; }
            var paths = string.Join("\n", _selected.OrderBy(s => s));
            Clipboard.SetText(paths);
            SetStatus($"คัดลอก {_selected.Count} path");
        }

        private void DeleteSelBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selected.Count == 0) { SetStatus("ไม่ได้เลือกรูป", false); return; }
            if (MessageBox.Show($"ลบ {_selected.Count} ไฟล์?", "ยืนยัน",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            int ok = 0;
            foreach (var fp in _selected.ToList())
            {
                try { File.Delete(fp); ok++; } catch { }
            }
            _selected.Clear();
            SelectAllCheck.IsChecked = false;
            LoadGallery();
            SetStatus($"ลบแล้ว {ok} ไฟล์");
        }

        // ── Capture Full Screen ─────────────────────────────────────
        private async void CaptureFullBtn_Click(object sender, RoutedEventArgs e)
        {
            await CaptureFullScreen();
        }

        private async Task CaptureFullScreen()
        {
            Directory.CreateDirectory(_saveDir);
            WindowState = WindowState.Minimized;
            await Task.Delay(400);

            try
            {
                var screen = CaptureScreen();
                if (screen == null) { SetStatus("Capture failed", false); return; }

                var fp = SaveImage(screen);
                WindowState = WindowState.Normal;
                Activate();
                LoadGallery();
                Clipboard.SetText(fp);
                SetStatus("บันทึก + คัดลอก path แล้ว");
            }
            catch (Exception ex)
            {
                WindowState = WindowState.Normal;
                SetStatus($"Error: {ex.Message}", false);
            }
        }

        private static BitmapSource? CaptureScreen()
        {
            var screenLeft = (int)SystemParameters.VirtualScreenLeft;
            var screenTop = (int)SystemParameters.VirtualScreenTop;
            var screenWidth = (int)SystemParameters.VirtualScreenWidth;
            var screenHeight = (int)SystemParameters.VirtualScreenHeight;

            using var bmp = new System.Drawing.Bitmap(screenWidth, screenHeight);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(screenLeft, screenTop, 0, 0, bmp.Size);

            var hBmp = bmp.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBmp);
            }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        // ── Capture Region ──────────────────────────────────────────
        private async void CaptureRegionBtn_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(_saveDir);
            WindowState = WindowState.Minimized;
            await Task.Delay(300);

            try
            {
                var fullBmp = CaptureScreen();
                if (fullBmp == null) { WindowState = WindowState.Normal; return; }

                var regionWin = new RegionSelectWindow(fullBmp);
                if (regionWin.ShowDialog() == true && regionWin.SelectedRect is Int32Rect rect && rect.Width > 10 && rect.Height > 10)
                {
                    var cropped = new CroppedBitmap(fullBmp, rect);
                    var fp = SaveImage(cropped);
                    WindowState = WindowState.Normal;
                    Activate();
                    LoadGallery();
                    Clipboard.SetText(fp);
                    SetStatus("บันทึก + คัดลอก path แล้ว");
                }
                else
                {
                    WindowState = WindowState.Normal;
                    SetStatus("พื้นที่เล็กเกินไป", false);
                }
            }
            catch (Exception ex)
            {
                WindowState = WindowState.Normal;
                SetStatus($"Error: {ex.Message}", false);
            }
        }

        // ── Paste Clipboard ─────────────────────────────────────────
        private void PasteBtn_Click(object sender, RoutedEventArgs e)
        {
            PasteClipboard();
        }

        private void PasteClipboard()
        {
            Directory.CreateDirectory(_saveDir);
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    var fp = SaveImage(img);
                    LoadGallery();
                    Clipboard.SetText(fp);
                    SetStatus("วางรูปจาก Clipboard + คัดลอก path");
                    return;
                }

                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    var exts = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif" };
                    int imported = 0;
                    foreach (string? f in files)
                    {
                        if (f != null && exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        {
                            var dest = Path.Combine(_saveDir, Path.GetFileName(f));
                            File.Copy(f, dest, true);
                            imported++;
                        }
                    }
                    if (imported > 0)
                    {
                        LoadGallery();
                        SetStatus($"วางแล้ว {imported} ไฟล์");
                        return;
                    }
                }

                if (Clipboard.ContainsText())
                {
                    var path = Clipboard.GetText().Trim().Trim('"');
                    var exts = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif" };
                    if (File.Exists(path) && exts.Contains(Path.GetExtension(path).ToLowerInvariant()))
                    {
                        var dest = Path.Combine(_saveDir, Path.GetFileName(path));
                        File.Copy(path, dest, true);
                        LoadGallery();
                        SetStatus("วางรูปจาก path");
                        return;
                    }
                }

                SetStatus("ไม่พบรูปใน Clipboard", false);
            }
            catch (Exception ex)
            {
                SetStatus($"Paste error: {ex.Message}", false);
            }
        }

        // ── Save Image ──────────────────────────────────────────────
        private string SaveImage(BitmapSource img)
        {
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fp = Path.Combine(_saveDir, $"screenshot_{ts}.png");

            using var fs = new FileStream(fp, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            encoder.Save(fs);

            return fp;
        }

        // ── Preview ─────────────────────────────────────────────────
        private void ShowPreview(string filepath)
        {
            var preview = new PreviewWindow(filepath, _isDark);
            preview.FileDeleted += () =>
            {
                _selected.Remove(filepath);
                LoadGallery();
            };
            preview.Owner = this;
            preview.Show();
        }

        // ── Copy Path ───────────────────────────────────────────────
        private void CopyOnePath(string filepath)
        {
            Clipboard.SetText(filepath);
            SetStatus("คัดลอกแล้ว: " + Path.GetFileName(filepath));
        }

        // ── Theme ───────────────────────────────────────────────────
        private void ThemeBtn_Click(object sender, RoutedEventArgs e)
        {
            _isDark = !_isDark;
            ApplyTheme();
            SaveConfig();
            LoadGallery();
        }

        private void ApplyTheme()
        {
            var borderBrush = new SolidColorBrush(_isDark ? Color.FromRgb(50, 50, 60) : Color.FromRgb(229, 229, 229));

            if (_isDark)
            {
                var bg1 = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                var bg2 = new SolidColorBrush(Color.FromRgb(39, 39, 39));
                var bg3 = new SolidColorBrush(Color.FromRgb(25, 25, 25));
                var fg = new SolidColorBrush(Color.FromRgb(230, 230, 230));
                var fgSub = new SolidColorBrush(Color.FromRgb(160, 160, 160));

                MainWin.Background = bg3;
                TitleBar.Background = bg1;
                ToolbarBorder.Background = bg2;
                ToolbarBorder.BorderBrush = borderBrush;
                AddressBar.Background = bg1;
                AddressBar.BorderBrush = borderBrush;
                GalleryBorder.Background = bg3;
                BottomBar.Background = bg1;
                BottomBar.BorderBrush = borderBrush;
                SelectAllCheck.Foreground = fg;
                PathLabel.Foreground = fgSub;
                ThemeBtn.Content = "\u263D";
            }
            else
            {
                MainWin.Background = Brushes.White;
                TitleBar.Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));
                ToolbarBorder.Background = new SolidColorBrush(Color.FromRgb(249, 249, 249));
                ToolbarBorder.BorderBrush = borderBrush;
                AddressBar.Background = Brushes.White;
                AddressBar.BorderBrush = borderBrush;
                GalleryBorder.Background = Brushes.White;
                BottomBar.Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));
                BottomBar.BorderBrush = borderBrush;
                SelectAllCheck.Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                PathLabel.Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85));
                ThemeBtn.Content = "\u2600";
            }

            RefreshAlbumTabs();
        }

        // ── Status ──────────────────────────────────────────────────
        private void SetStatus(string text, bool ok = true)
        {
            StatusLabel.Text = text;
            StatusLabel.Foreground = new SolidColorBrush(ok
                ? Color.FromRgb(16, 124, 16)
                : Color.FromRgb(196, 43, 28));
        }

        // ── Keyboard shortcuts ──────────────────────────────────────
        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.F5:
                    await CaptureFullScreen();
                    break;
                case Key.Delete:
                    DeleteSelBtn_Click(sender, e);
                    break;
                case Key.V when Keyboard.Modifiers == ModifierKeys.Control:
                    PasteClipboard();
                    break;
                case Key.Escape:
                    Close();
                    break;
            }
        }
    }
}
