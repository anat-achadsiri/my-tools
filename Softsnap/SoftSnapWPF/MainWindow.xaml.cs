using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        private List<string> _albumOrder = new(); // MRU order (most recent first)
        private List<string> _pinnedAlbums = new(); // ปักหมุด — แสดงหน้าสุดเสมอ ไม่โดน MRU ดัน
        private readonly Dictionary<string, int> _albumCounts = new(); // cache จำนวนรูปต่อ album
        private List<string> _popupFiltered = new(); // album ที่ผ่าน filter (ตามลำดับที่แสดงใน popup)
        private readonly List<Border> _popupRows = new(); // แถวใน popup (คู่กับ _popupFiltered)
        private int _popupSelIndex = 0; // แถวที่ highlight ด้วยคีย์บอร์ด
        private readonly HashSet<string> _selected = new();
        private bool _isDark = true;
        private bool _sortNewestFirst = true; // true = ใหม่→เก่า, false = เก่า→ใหม่
        private const int ThumbSize = 140;
        private const int Columns = 3;
        private int _galleryGeneration = 0; // cancel stale loads

        // ── Drag-select state ───────────────────────────────────────
        private bool _isDragging;
        private Point _dragStart;
        private readonly Dictionary<Border, string> _cardToFile = new();

        // ── Grouping (ป้ายกำกับ = โฟลเดอร์ย่อยจริง) ──────────────────
        private static readonly string[] ImgExts = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
        private string? _activeGroup;                                   // โฟลเดอร์กลุ่มที่กำลังบันทึกลง (null = ราก Album)
        private Dictionary<string, string> _activeGroups = new();       // album name -> active group dir
        private readonly HashSet<string> _collapsedGroups = new();      // โฟลเดอร์กลุ่มที่ถูกยุบ
        private readonly HashSet<string> _hiddenGroups = new();         // โฟลเดอร์กลุ่มที่ถูกซ่อน (ไม่ลบ folder จริง)
        private bool _showHiddenGroups = false;                         // แสดงกลุ่มที่ซ่อนชั่วคราวหรือไม่
        private readonly HashSet<string> _migratedAlbums = new();       // ราก Album ที่ migrate แล้ว
        private readonly Dictionary<string, Border> _groupHeaders = new();     // group dir -> header card
        private readonly Dictionary<string, TextBlock> _groupBadges = new();   // group dir -> active badge

        // ── Folder navigation (แสดงโฟลเดอร์ในอัลบัมแบบ Explorer) ─────
        private string _currentDir = "";                                  // โฟลเดอร์ที่กำลังเปิดดูอยู่ (ราก = _saveDir)
        private readonly Dictionary<string, string> _currentDirs = new(); // album -> โฟลเดอร์ล่าสุดที่เปิด (จำเฉพาะระหว่างเปิดแอป)
        private readonly HashSet<string> _expandedDirs = new(StringComparer.OrdinalIgnoreCase); // โฟลเดอร์ที่ขยายอยู่ใน tree
        private bool _treeSyncing;                                        // กัน event วนตอน rebuild/sync tree

        public MainWindow()
        {
            InitializeComponent();

            _appDir = AppDomain.CurrentDomain.BaseDirectory;

            // Set window icon (BitmapFrame reads all .ico frames — WPF picks the best size per context)
            var icoPath = Path.Combine(_appDir, "softsnap_logo_v4.ico");
            if (File.Exists(icoPath))
            {
                Icon = BitmapFrame.Create(new Uri(icoPath, UriKind.Absolute));
            }
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

            // Load MRU album order
            if (_cfg.TryGetValue("album_order", out var orderVal) && orderVal is JsonElement oe && oe.ValueKind == JsonValueKind.Array)
            {
                _albumOrder = new List<string>();
                foreach (var item in oe.EnumerateArray())
                    if (item.GetString() is string s) _albumOrder.Add(s);
            }

            _pinnedAlbums = ReadStringArray("pinned_albums");

            // Load grouping state
            _activeGroups = ReadStringDict("active_groups");
            foreach (var p in ReadStringArray("collapsed_groups")) _collapsedGroups.Add(p);
            foreach (var p in ReadStringArray("hidden_groups")) _hiddenGroups.Add(p);
            foreach (var p in ReadStringArray("migrated_albums")) _migratedAlbums.Add(p);

            ApplyTheme();
        }

        private List<string> ReadStringArray(string key)
        {
            var list = new List<string>();
            if (_cfg.TryGetValue(key, out var v) && v is JsonElement e && e.ValueKind == JsonValueKind.Array)
                foreach (var it in e.EnumerateArray())
                    if (it.GetString() is string s) list.Add(s);
            return list;
        }

        private Dictionary<string, string> ReadStringDict(string key)
        {
            var d = new Dictionary<string, string>();
            if (_cfg.TryGetValue(key, out var v) && v is JsonElement e && e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                    if (p.Value.GetString() is string s) d[p.Name] = s;
            return d;
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
            _cfg["album_order"] = _albumOrder;
            _cfg["pinned_albums"] = _pinnedAlbums;
            _cfg["active_groups"] = _activeGroups;
            _cfg["collapsed_groups"] = _collapsedGroups.ToList();
            _cfg["hidden_groups"] = _hiddenGroups.ToList();
            _cfg["migrated_albums"] = _migratedAlbums.ToList();

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

            // Sync album order: remove deleted albums, add new ones at end
            _albumOrder.RemoveAll(n => !_albums.ContainsKey(n));
            _pinnedAlbums.RemoveAll(n => !_albums.ContainsKey(n));
            foreach (var name in _albums.Keys)
                if (!_albumOrder.Contains(name)) _albumOrder.Add(name);

            // Ensure current album is at top of MRU
            if (!string.IsNullOrEmpty(_currentAlbum) && _albums.ContainsKey(_currentAlbum))
            {
                _albumOrder.Remove(_currentAlbum);
                _albumOrder.Insert(0, _currentAlbum);
            }

            if (_albums.TryGetValue(_currentAlbum, out var dir))
                _saveDir = dir;

            Directory.CreateDirectory(_saveDir);

            _currentDir = _saveDir;

            _activeGroup = _activeGroups.TryGetValue(_currentAlbum, out var ag) && Directory.Exists(ag)
                && SameDir(Path.GetDirectoryName(ag) ?? "", _saveDir) ? ag : null;
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

        /// <summary>\u0E25\u0E33\u0E14\u0E31\u0E1A\u0E41\u0E2A\u0E14\u0E07\u0E1C\u0E25: \u0E1B\u0E31\u0E01\u0E2B\u0E21\u0E38\u0E14\u0E01\u0E48\u0E2D\u0E19 (\u0E15\u0E32\u0E21\u0E25\u0E33\u0E14\u0E31\u0E1A\u0E17\u0E35\u0E48\u0E1B\u0E31\u0E01) \u0E41\u0E25\u0E49\u0E27\u0E15\u0E32\u0E21\u0E14\u0E49\u0E27\u0E22 MRU \u0E02\u0E2D\u0E07\u0E17\u0E35\u0E48\u0E40\u0E2B\u0E25\u0E37\u0E2D</summary>
        private List<string> GetOrderedAlbums()
        {
            var pinned = _pinnedAlbums.Where(n => _albums.ContainsKey(n)).ToList();
            var rest = _albumOrder.Where(n => _albums.ContainsKey(n) && !pinned.Contains(n));
            return pinned.Concat(rest).ToList();
        }

        private void RefreshAlbumTabs()
        {
            AlbumTabs.Items.Clear();

            var albumNames = GetOrderedAlbums();
            var visible = albumNames.Take(MaxVisibleTabs).ToList();
            var overflowCount = albumNames.Count - visible.Count;

            foreach (var name in visible)
                AlbumTabs.Items.Add(CreateAlbumPill(name));

            // Overflow dropdown button
            if (overflowCount > 0)
            {
                AlbumOverflowBtn.Visibility = Visibility.Visible;
                AlbumOverflowText.Text = $"\u25BE +{overflowCount}";
                AlbumOverflowBtn.Background = new SolidColorBrush(_isDark
                    ? Color.FromRgb(45, 45, 50)
                    : Color.FromRgb(235, 235, 240));
                AlbumOverflowText.Foreground = new SolidColorBrush(_isDark
                    ? Color.FromRgb(180, 180, 190)
                    : Color.FromRgb(85, 85, 85));
            }
            else
            {
                AlbumOverflowBtn.Visibility = Visibility.Collapsed;
            }

            // ปุ่มสร้าง album ใหม่ (ต่อท้าย +N)
            AddAlbumPill.Background = new SolidColorBrush(_isDark
                ? Color.FromRgb(45, 45, 50)
                : Color.FromRgb(235, 235, 240));
            AddAlbumPillText.Foreground = new SolidColorBrush(_isDark
                ? Color.FromRgb(180, 180, 190)
                : Color.FromRgb(85, 85, 85));

            // Popup chrome (\u0E2A\u0E23\u0E49\u0E32\u0E07\u0E40\u0E2A\u0E21\u0E2D \u0E40\u0E1E\u0E37\u0E48\u0E2D\u0E43\u0E2B\u0E49 Ctrl+K \u0E43\u0E0A\u0E49\u0E44\u0E14\u0E49\u0E41\u0E21\u0E49 album \u0E19\u0E49\u0E2D\u0E22)
            AlbumPopupBorder.Background = new SolidColorBrush(_isDark
                ? Color.FromRgb(40, 40, 45) : Colors.White);
            AlbumPopupBorder.BorderBrush = new SolidColorBrush(_isDark
                ? Color.FromRgb(60, 60, 65) : Color.FromRgb(224, 224, 224));
            AlbumSearchBorder.Background = new SolidColorBrush(_isDark
                ? Color.FromRgb(30, 30, 34) : Colors.White);
            AlbumSearchBorder.BorderBrush = new SolidColorBrush(_isDark
                ? Color.FromRgb(70, 70, 78) : Color.FromRgb(213, 213, 213));
            AlbumSearchBox.Foreground = new SolidColorBrush(_isDark
                ? Color.FromRgb(230, 230, 230) : Color.FromRgb(30, 30, 30));
            AlbumSearchBox.CaretBrush = AlbumSearchBox.Foreground;
            AlbumSearchIcon.Foreground = new SolidColorBrush(_isDark
                ? Color.FromRgb(180, 180, 190) : Color.FromRgb(85, 85, 85));

            RefreshAlbumPopup();
        }

        /// <summary>\u0E2A\u0E23\u0E49\u0E32\u0E07\u0E23\u0E32\u0E22\u0E01\u0E32\u0E23\u0E43\u0E19 popup \u0E15\u0E32\u0E21\u0E04\u0E33\u0E04\u0E49\u0E19\u0E2B\u0E32 \u2014 \u0E41\u0E1A\u0E48\u0E07 section \u0E1B\u0E31\u0E01\u0E2B\u0E21\u0E38\u0E14 / \u0E17\u0E31\u0E49\u0E07\u0E2B\u0E21\u0E14</summary>
        private void RefreshAlbumPopup()
        {
            AlbumPopupItems.Items.Clear();
            _popupFiltered.Clear();
            _popupRows.Clear();

            var filter = (AlbumSearchBox.Text ?? "").Trim();
            var albumNames = GetOrderedAlbums();
            var matched = albumNames
                .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var pinnedMatched = matched.Where(n => _pinnedAlbums.Contains(n)).ToList();
            var restMatched = matched.Where(n => !_pinnedAlbums.Contains(n)).ToList();

            if (pinnedMatched.Count > 0)
            {
                AlbumPopupItems.Items.Add(CreatePopupSectionHeader("\uD83D\uDCCC \u0E1B\u0E31\u0E01\u0E2B\u0E21\u0E38\u0E14"));
                foreach (var name in pinnedMatched)
                    AddPopupRow(name);
            }
            if (restMatched.Count > 0)
            {
                if (pinnedMatched.Count > 0)
                    AlbumPopupItems.Items.Add(CreatePopupSectionHeader(filter.Length == 0 ? "\u0E25\u0E48\u0E32\u0E2A\u0E38\u0E14" : "\u0E2D\u0E37\u0E48\u0E19 \u0E46"));
                foreach (var name in restMatched)
                    AddPopupRow(name);
            }
            if (matched.Count == 0)
            {
                AlbumPopupItems.Items.Add(new TextBlock
                {
                    Text = "\u0E44\u0E21\u0E48\u0E1E\u0E1A album \u0E17\u0E35\u0E48\u0E15\u0E23\u0E07\u0E01\u0E31\u0E1A\u0E04\u0E33\u0E04\u0E49\u0E19\u0E2B\u0E32",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Margin = new Thickness(12, 8, 12, 8),
                    Foreground = new SolidColorBrush(_isDark
                        ? Color.FromRgb(140, 140, 150) : Color.FromRgb(130, 130, 130))
                });
            }

            if (_popupSelIndex >= _popupFiltered.Count) _popupSelIndex = 0;
            UpdatePopupHighlight();
        }

        private TextBlock CreatePopupSectionHeader(string text) => new()
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 6, 10, 2),
            Foreground = new SolidColorBrush(_isDark
                ? Color.FromRgb(140, 140, 150) : Color.FromRgb(130, 130, 130))
        };

        private void AddPopupRow(string name)
        {
            var isActive = name == _currentAlbum;
            var isPinned = _pinnedAlbums.Contains(name);

            var row = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 8, 6),
                Margin = new Thickness(2, 1, 2, 1),
                Cursor = Cursors.Hand,
                Tag = name
            };

            var panel = new DockPanel { LastChildFill = true };

            // \u0E1B\u0E38\u0E48\u0E21\u0E1B\u0E31\u0E01\u0E2B\u0E21\u0E38\u0E14 (\u0E02\u0E27\u0E32\u0E2A\u0E38\u0E14)
            var pinBtn = new TextBlock
            {
                Text = "\uD83D\uDCCC",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Opacity = isPinned ? 1.0 : 0.25,
                Cursor = Cursors.Hand,
                ToolTip = isPinned ? "\u0E16\u0E2D\u0E19\u0E2B\u0E21\u0E38\u0E14" : "\u0E1B\u0E31\u0E01\u0E2B\u0E21\u0E38\u0E14\u0E44\u0E27\u0E49\u0E2B\u0E19\u0E49\u0E32\u0E2A\u0E38\u0E14"
            };
            DockPanel.SetDock(pinBtn, Dock.Right);
            pinBtn.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true; // \u0E2D\u0E22\u0E48\u0E32\u0E43\u0E2B\u0E49\u0E17\u0E30\u0E25\u0E38\u0E44\u0E1B\u0E40\u0E1B\u0E25\u0E35\u0E48\u0E22\u0E19 album
                TogglePinAlbum(name);
            };
            panel.Children.Add(pinBtn);

            // \u0E08\u0E33\u0E19\u0E27\u0E19\u0E23\u0E39\u0E1B (\u0E02\u0E27\u0E32)
            var countLbl = new TextBlock
            {
                Text = _albumCounts.TryGetValue(name, out var c) ? c.ToString() : "",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Foreground = new SolidColorBrush(isActive
                    ? Color.FromArgb(200, 255, 255, 255)
                    : (_isDark ? Color.FromRgb(140, 140, 150) : Color.FromRgb(140, 140, 140)))
            };
            DockPanel.SetDock(countLbl, Dock.Right);
            panel.Children.Add(countLbl);
            UpdateAlbumCountAsync(name, countLbl);

            // \u0E0A\u0E37\u0E48\u0E2D album
            var lbl = new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(isActive
                    ? Colors.White
                    : (_isDark ? Color.FromRgb(220, 220, 220) : Color.FromRgb(30, 30, 30)))
            };
            panel.Children.Add(lbl);

            row.Child = panel;

            var albumName = name;
            row.MouseLeftButtonDown += (_, _) => SelectAlbumFromPopup(albumName);
            var rowIndex = _popupFiltered.Count;
            row.MouseEnter += (_, _) =>
            {
                _popupSelIndex = rowIndex;
                UpdatePopupHighlight();
            };

            _popupFiltered.Add(name);
            _popupRows.Add(row);
            AlbumPopupItems.Items.Add(row);
        }

        private void SelectAlbumFromPopup(string name)
        {
            AlbumPopup.IsOpen = false;
            if (name != _currentAlbum)
                AlbumCombo.SelectedItem = name;
        }

        private void TogglePinAlbum(string name)
        {
            if (!_pinnedAlbums.Remove(name))
                _pinnedAlbums.Add(name);
            SaveConfig();
            RefreshAlbumTabs(); // \u0E2A\u0E23\u0E49\u0E32\u0E07 pills + popup \u0E43\u0E2B\u0E21\u0E48 (\u0E04\u0E33\u0E04\u0E49\u0E19\u0E2B\u0E32\u0E43\u0E19 box \u0E04\u0E07\u0E40\u0E14\u0E34\u0E21)
        }

        private void UpdatePopupHighlight()
        {
            for (int i = 0; i < _popupRows.Count; i++)
            {
                var row = _popupRows[i];
                var isActive = (string)row.Tag == _currentAlbum;
                var isHighlight = i == _popupSelIndex;
                row.Background = new SolidColorBrush(
                    isActive ? (_isDark ? Color.FromRgb(55, 55, 75) : Color.FromRgb(0, 120, 215))
                    : isHighlight ? (_isDark ? Color.FromRgb(50, 50, 55) : Color.FromRgb(238, 240, 245))
                    : Colors.Transparent);
            }
        }

        /// <summary>\u0E19\u0E31\u0E1A\u0E08\u0E33\u0E19\u0E27\u0E19\u0E23\u0E39\u0E1B\u0E43\u0E19 album \u0E41\u0E1A\u0E1A background + cache (\u0E01\u0E31\u0E19\u0E0A\u0E49\u0E32\u0E01\u0E31\u0E1A\u0E42\u0E1F\u0E25\u0E40\u0E14\u0E2D\u0E23\u0E4C\u0E43\u0E2B\u0E0D\u0E48/network)</summary>
        private void UpdateAlbumCountAsync(string name, TextBlock lbl)
        {
            if (_albumCounts.ContainsKey(name)) return; // \u0E21\u0E35 cache \u0E41\u0E25\u0E49\u0E27 \u2014 set \u0E44\u0E1B\u0E15\u0E2D\u0E19\u0E2A\u0E23\u0E49\u0E32\u0E07 lbl \u0E41\u0E25\u0E49\u0E27
            if (!_albums.TryGetValue(name, out var dir)) return;
            Task.Run(() =>
            {
                int cnt = 0;
                try
                {
                    cnt = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Count(f => ImgExts.Contains(Path.GetExtension(f).ToLowerInvariant()));
                }
                catch { /* \u0E42\u0E1F\u0E25\u0E40\u0E14\u0E2D\u0E23\u0E4C\u0E2B\u0E32\u0E22/\u0E44\u0E21\u0E48\u0E21\u0E35\u0E2A\u0E34\u0E17\u0E18\u0E34\u0E4C \u2014 \u0E41\u0E2A\u0E14\u0E07\u0E27\u0E48\u0E32\u0E07\u0E44\u0E27\u0E49 */ }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _albumCounts[name] = cnt;
                    lbl.Text = cnt.ToString();
                }));
            });
        }

        private void OpenAlbumPopup()
        {
            _albumCounts.Clear(); // \u0E19\u0E31\u0E1A\u0E43\u0E2B\u0E21\u0E48\u0E17\u0E38\u0E01\u0E04\u0E23\u0E31\u0E49\u0E07\u0E17\u0E35\u0E48\u0E40\u0E1B\u0E34\u0E14 (async \u0E44\u0E21\u0E48\u0E1A\u0E25\u0E47\u0E2D\u0E01 UI)
            AlbumSearchBox.Text = "";
            _popupSelIndex = 0;
            RefreshAlbumPopup();
            AlbumPopup.PlacementTarget = AlbumOverflowBtn.Visibility == Visibility.Visible
                ? AlbumOverflowBtn : (UIElement)AlbumTabs;
            AlbumPopup.IsOpen = true;
            Dispatcher.BeginInvoke(new Action(() => AlbumSearchBox.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void AlbumSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _popupSelIndex = 0;
            RefreshAlbumPopup();
        }

        private void AlbumSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (_popupFiltered.Count > 0)
                        _popupSelIndex = (_popupSelIndex + 1) % _popupFiltered.Count;
                    UpdatePopupHighlight();
                    e.Handled = true;
                    break;
                case Key.Up:
                    if (_popupFiltered.Count > 0)
                        _popupSelIndex = (_popupSelIndex - 1 + _popupFiltered.Count) % _popupFiltered.Count;
                    UpdatePopupHighlight();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (_popupSelIndex >= 0 && _popupSelIndex < _popupFiltered.Count)
                        SelectAlbumFromPopup(_popupFiltered[_popupSelIndex]);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    AlbumPopup.IsOpen = false;
                    e.Handled = true;
                    break;
            }
        }

        private Border CreateAlbumPill(string name)
        {
            var isActive = name == _currentAlbum;
            var isPinned = _pinnedAlbums.Contains(name);

            var pill = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                ToolTip = isPinned ? "คลิกขวาเพื่อถอนหมุด" : "คลิกขวาเพื่อปักหมุดไว้หน้าสุด",
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
                Text = isPinned ? "📌 " + name : name,
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
            pill.MouseRightButtonDown += (_, e) =>
            {
                e.Handled = true;
                TogglePinAlbum(albumName);
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
            if (AlbumPopup.IsOpen)
                AlbumPopup.IsOpen = false;
            else
                OpenAlbumPopup();
        }

        private void AddAlbumPill_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AddAlbumBtn_Click(sender, e);
        }

        private void BumpAlbumOrder(string name)
        {
            _albumOrder.Remove(name);
            _albumOrder.Insert(0, name);
        }

        private void AlbumCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AlbumCombo.SelectedItem is string name && _albums.TryGetValue(name, out var dir))
            {
                _currentAlbum = name;
                _saveDir = dir;
                BumpAlbumOrder(name);
                Directory.CreateDirectory(_saveDir);
                SaveConfig();
                RefreshAlbumTabs();
                // กลับไปยังโฟลเดอร์ล่าสุดที่เปิดไว้ในอัลบัมนี้ (ถ้ายังอยู่) — NavigateTo จัดการ active group ให้
                NavigateTo(_currentDirs.TryGetValue(name, out var last) ? last : dir);
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
            _currentDir = albumPath;
            _activeGroup = null;
            BumpAlbumOrder(name);
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
            _currentDir = dlg.SelectedPath;
            _activeGroup = null;
            BumpAlbumOrder(name);
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
            _albumOrder.Remove(name);
            _activeGroups.Remove(name);
            _currentDirs.Remove(name);
            var first = _albumOrder.FirstOrDefault() ?? _albums.Keys.First();
            _currentAlbum = first;
            _saveDir = _albums[first];
            _currentDir = _currentDirs.TryGetValue(first, out var lastDir) ? lastDir : _saveDir;
            _activeGroup = _activeGroups.TryGetValue(first, out var ag) && Directory.Exists(ag)
                && SameDir(Path.GetDirectoryName(ag) ?? "", CurrentViewDir()) ? ag : null;
            SaveConfig();

            _selected.Clear();
            RefreshAlbumCombo();
            LoadGallery();
            SetStatus($"ลบ Album: {name}");
        }

        private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var dir = CurrentViewDir();
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", dir);
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            _selected.Clear();
            _albumCounts.Clear();
            LoadGallery();
            SetStatus("Refreshed");
        }

        // ── Grouping helpers ────────────────────────────────────────
        // โฟลเดอร์ปลายทางสำหรับบันทึกรูปใหม่ (กลุ่ม active หรือโฟลเดอร์ที่เปิดดูอยู่)
        // กลุ่มที่ถูกยุบจะไม่รับรูปใหม่ — บันทึกลงโฟลเดอร์ที่เปิดอยู่แทน
        private string CurrentSaveDir()
            => (!string.IsNullOrEmpty(_activeGroup) && Directory.Exists(_activeGroup) && !_collapsedGroups.Contains(_activeGroup))
                ? _activeGroup! : CurrentViewDir();

        private List<string> SortFiles(IEnumerable<string> files)
            => (_sortNewestFirst
                ? files.OrderByDescending(File.GetLastWriteTime)
                : files.OrderBy(File.GetLastWriteTime)).ToList();

        private static bool IsImage(string f) => ImgExts.Contains(Path.GetExtension(f).ToLowerInvariant());

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                var p = Path.Combine(dir, $"{name}_{i}{ext}");
                if (!File.Exists(p)) return p;
            }
        }

        private static string UniqueDir(string path)
        {
            if (!Directory.Exists(path)) return path;
            for (int i = 1; ; i++)
            {
                var p = $"{path}_{i}";
                if (!Directory.Exists(p)) return p;
            }
        }

        private static string? ParseTs(string filename)
        {
            var m = System.Text.RegularExpressions.Regex.Match(filename, @"(\d{8}_\d{6})");
            return m.Success ? m.Value : null;
        }

        // Auto-migrate: แปลงป้าย+รูปแบบเรียงตามเวลา (ของเดิม) ให้เป็นโฟลเดอร์กลุ่มจริง — ทำครั้งเดียวต่อ Album
        private void MigrateAlbumIfNeeded(string albumRoot)
        {
            if (_migratedAlbums.Contains(albumRoot)) return;

            List<string> rootFiles;
            try
            {
                rootFiles = Directory.GetFiles(albumRoot).Where(IsImage)
                    .OrderBy(File.GetLastWriteTime).ToList();
            }
            catch { return; }

            // migrate เฉพาะ Album ที่เคยใช้ป้ายกำกับ (มีไฟล์ _label หลุดอยู่ในราก) เท่านั้น
            if (!rootFiles.Any(IsLabelFile))
            {
                _migratedAlbums.Add(albumRoot);
                SaveConfig();
                return;
            }

            string? curGroup = null;
            foreach (var f in rootFiles)
            {
                try
                {
                    if (IsLabelFile(f))
                    {
                        var ts = ParseTs(Path.GetFileName(f)) ?? File.GetLastWriteTime(f).ToString("yyyyMMdd_HHmmss");
                        curGroup = UniqueDir(Path.Combine(albumRoot, $"grp_{ts}"));
                        Directory.CreateDirectory(curGroup);
                        File.Move(f, Path.Combine(curGroup, "_label.png"));
                    }
                    else if (curGroup != null)
                    {
                        // รูปที่อยู่ถัดจากป้าย → ย้ายเข้ากลุ่มนั้น (รูปก่อนป้ายแรกคงไว้ในรากเป็น "ไม่จัดกลุ่ม")
                        File.Move(f, UniquePath(Path.Combine(curGroup, Path.GetFileName(f))));
                    }
                }
                catch { /* ข้ามไฟล์ที่ย้ายไม่ได้ */ }
            }

            _migratedAlbums.Add(albumRoot);
            SaveConfig();
            SetStatus("จัดกลุ่มข้อมูลเดิมเรียบร้อย");
        }

        // ── Folder navigation (แสดงโฟลเดอร์ในอัลบัมแบบ Explorer) ─────
        private static bool IsGroupDir(string d)
            => Path.GetFileName(d).StartsWith("grp_", StringComparison.OrdinalIgnoreCase);

        private static string NormDir(string d)
            => Path.GetFullPath(d).TrimEnd('\\', '/');

        private static bool SameDir(string a, string b)
            => string.Equals(NormDir(a), NormDir(b), StringComparison.OrdinalIgnoreCase);

        // โฟลเดอร์ที่กำลังเปิดดูอยู่ (validate ทุกครั้ง — ถ้าโฟลเดอร์หาย/อยู่นอกอัลบัม ให้กลับราก)
        private string CurrentViewDir()
        {
            if (string.IsNullOrEmpty(_currentDir) || !Directory.Exists(_currentDir) || !IsUnderAlbum(_currentDir))
                _currentDir = _saveDir;
            return _currentDir;
        }

        private bool IsUnderAlbum(string dir)
        {
            var root = NormDir(_saveDir);
            var d = NormDir(dir);
            return d.Equals(root, StringComparison.OrdinalIgnoreCase)
                || d.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private void NavigateTo(string dir)
        {
            if (!Directory.Exists(dir) || !IsUnderAlbum(dir)) dir = _saveDir;
            _currentDir = dir;
            _currentDirs[_currentAlbum] = dir;

            // กลุ่ม active ใช้ได้เฉพาะเมื่ออยู่ในโฟลเดอร์ที่เปิดอยู่ — กลับมาโฟลเดอร์เดิมจะ active ให้อัตโนมัติ
            _activeGroup = _activeGroups.TryGetValue(_currentAlbum, out var ag)
                && Directory.Exists(ag)
                && SameDir(Path.GetDirectoryName(ag) ?? "", dir)
                ? ag : null;

            _selected.Clear();
            SelectAllCheck.IsChecked = false;
            LoadGallery();
        }

        private void NavigateUp()
        {
            var cur = CurrentViewDir();
            if (SameDir(cur, _saveDir)) return;
            NavigateTo(Path.GetDirectoryName(NormDir(cur)) ?? _saveDir);
        }

        // ── Folder tree sidebar (explorer-style) ────────────────────
        private void BuildFolderTree()
        {
            _treeSyncing = true;
            try
            {
                FolderTree.Items.Clear();
                var root = CreateTreeNode(_saveDir, _currentAlbum, isRoot: true);
                PopulateTreeChildren(root);
                root.IsExpanded = true;
                FolderTree.Items.Add(root);
                SelectTreeNode(root, NormDir(CurrentViewDir()));
            }
            finally { _treeSyncing = false; }
            UpdateTreeCountAsync();
        }

        private TreeViewItem CreateTreeNode(string dir, string label, bool isRoot = false)
        {
            var normDir = NormDir(dir);
            var fg = new SolidColorBrush(_isDark ? Color.FromRgb(220, 220, 230) : Color.FromRgb(30, 30, 30));

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                // ราก = อัลบัม ใช้ 🗂️ ให้ต่างจากโฟลเดอร์ย่อย 📁
                Text = isRoot ? "\U0001F5C2️" : "\U0001F4C1",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            header.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = isRoot ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            });

            var node = new TreeViewItem
            {
                Header = header,
                Tag = normDir,
                Padding = new Thickness(2, 3, 2, 3)
            };

            // lazy-load: ใส่ dummy ไว้ก่อนถ้ามีโฟลเดอร์ย่อย — โหลดจริงตอนขยาย
            if (!isRoot)
            {
                bool hasSubs = false;
                try { hasSubs = Directory.EnumerateDirectories(dir).Any(); } catch { }
                if (hasSubs)
                {
                    if (_expandedDirs.Contains(normDir))
                    {
                        PopulateTreeChildren(node);
                        node.IsExpanded = true;
                    }
                    else
                        node.Items.Add(new TreeViewItem { Tag = "…dummy…" });
                }
            }

            node.Expanded += (s, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, node)) return;
                _expandedDirs.Add(normDir);
                if (node.Items.Count == 1 && node.Items[0] is TreeViewItem d && (d.Tag as string) == "…dummy…")
                    PopulateTreeChildren(node);
            };
            node.Collapsed += (s, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, node)) return;
                _expandedDirs.Remove(normDir);
            };

            // เมนูคลิกขวา (ราก Album ไม่ให้เปลี่ยนชื่อ)
            var menu = new ContextMenu();
            var miNewFolder = new MenuItem { Header = "สร้างโฟลเดอร์ใหม่..." };
            miNewFolder.Click += (_, _) => CreateNewFolder(normDir);
            menu.Items.Add(miNewFolder);
            var miOpen = new MenuItem { Header = "เปิดใน Explorer" };
            miOpen.Click += (_, _) => Process.Start("explorer.exe", normDir);
            menu.Items.Add(miOpen);
            if (!isRoot)
            {
                var miRename = new MenuItem { Header = "เปลี่ยนชื่อ..." };
                miRename.Click += (_, _) => RenameFolder(normDir);
                menu.Items.Add(miRename);
            }
            node.ContextMenu = menu;

            return node;
        }

        private void PopulateTreeChildren(TreeViewItem node)
        {
            node.Items.Clear();
            var dir = (string)node.Tag;
            List<string> subs;
            try { subs = Directory.GetDirectories(dir).ToList(); } catch { return; }

            // โฟลเดอร์ปกติ (เรียงชื่อ) มาก่อน แล้วตามด้วยกลุ่ม grp_* (เรียงตามเวลาสร้าง)
            var ordered = subs.Where(d => !IsGroupDir(d))
                    .OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)
                .Concat(subs.Where(IsGroupDir)
                    .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase));
            foreach (var d in ordered)
                node.Items.Add(CreateTreeNode(d, Path.GetFileName(d)));
        }

        // ไล่หา node ของโฟลเดอร์ที่เปิดอยู่ — ขยาย ancestor ให้ครบแล้ว select
        private void SelectTreeNode(TreeViewItem node, string targetDir)
        {
            var nodeDir = (string)node.Tag;
            if (string.Equals(nodeDir, targetDir, StringComparison.OrdinalIgnoreCase))
            {
                node.IsSelected = true;
                node.BringIntoView();
                return;
            }
            if (!targetDir.StartsWith(nodeDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;

            if (node.Items.Count == 1 && node.Items[0] is TreeViewItem d && (d.Tag as string) == "…dummy…")
                PopulateTreeChildren(node);
            node.IsExpanded = true;
            foreach (var child in node.Items.OfType<TreeViewItem>().ToList())
                SelectTreeNode(child, targetDir);
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_treeSyncing) return;
            if (e.NewValue is TreeViewItem item && item.Tag is string dir && dir != "…dummy…" && Directory.Exists(dir))
            {
                if (SameDir(dir, CurrentViewDir())) return;
                // เลื่อนไปทำหลังจบ event — กันการ rebuild tree ระหว่าง selection กำลังเปลี่ยน
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NavigateTo(dir);
                    SetStatus("เปิดโฟลเดอร์: " + (SameDir(dir, _saveDir) ? _currentAlbum : Path.GetFileName(dir)));
                }));
            }
        }

        // นับรูปทั้งอัลบัม (รวมทุกชั้น ไม่นับป้าย) แสดงมุมล่างของ sidebar
        private void UpdateTreeCountAsync()
        {
            var dir = _saveDir;
            var gen = _galleryGeneration;
            Task.Run(() =>
            {
                int cnt = 0;
                try
                {
                    cnt = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Count(f => IsImage(f) && !IsLabelFile(f));
                }
                catch { /* ไม่มีสิทธิ์/โฟลเดอร์หาย — แสดงว่างไว้ */ }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_galleryGeneration == gen) TreeCountLabel.Text = $"{cnt} items";
                }));
            });
        }

        // เปลี่ยนชื่อโฟลเดอร์ย่อย — ใช้ได้ทั้งโฟลเดอร์ปกติและโฟลเดอร์กลุ่ม grp_*
        // เปลี่ยนชื่อกลุ่ม grp_* เป็นชื่อธรรมดา = เลิกใช้ป้ายกำกับ (ลบไฟล์ _label.png ให้อัตโนมัติ)
        private void RenameFolder(string dir)
        {
            var oldName = Path.GetFileName(dir);
            bool wasGroup = IsGroupDir(dir);
            var title = wasGroup ? "ตั้งชื่อโฟลเดอร์ (แทนป้ายกำกับ)" : "เปลี่ยนชื่อโฟลเดอร์";
            var dlg = new InputDialog(title, "ชื่อใหม่:", wasGroup ? "" : oldName) { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Answer)) return;

            var newName = dlg.Answer.Trim();
            if (newName == oldName) return;
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            { SetStatus("ชื่อโฟลเดอร์มีอักขระที่ใช้ไม่ได้", false); return; }
            if (newName.StartsWith("grp_", StringComparison.OrdinalIgnoreCase))
            { SetStatus("ชื่อขึ้นต้นด้วย grp_ สงวนไว้สำหรับกลุ่มป้ายกำกับ", false); return; }

            var newDir = Path.Combine(Path.GetDirectoryName(NormDir(dir))!, newName);
            if (Directory.Exists(newDir)) { SetStatus("มีโฟลเดอร์ชื่อนี้อยู่แล้ว", false); return; }
            try { Directory.Move(dir, newDir); }
            catch (Exception ex) { SetStatus("เปลี่ยนชื่อไม่สำเร็จ: " + ex.Message, false); return; }

            // อัปเดต state ทุกตัวที่อ้าง path เดิม (กลุ่ม active, ยุบ/ซ่อน, โฟลเดอร์ที่เปิดอยู่, tree)
            RemapDirState(dir, newDir);

            if (wasGroup)
            {
                // เลิกเป็นกลุ่มป้ายกำกับ → เอาไฟล์ป้ายออก (ชื่อโฟลเดอร์แทนป้ายแล้ว)
                try
                {
                    var lbl = Path.Combine(newDir, "_label.png");
                    if (File.Exists(lbl)) File.Delete(lbl);
                }
                catch { /* ลบป้ายไม่ได้ — ข้ามไป ผู้ใช้ลบเองได้ */ }

                // ไม่ใช่กลุ่มแล้ว — เลิกเป็นปลายทางบันทึกแบบกลุ่ม
                if (!string.IsNullOrEmpty(_activeGroup) && SameDir(_activeGroup!, newDir))
                {
                    _activeGroup = null;
                    _activeGroups.Remove(_currentAlbum);
                }
                _collapsedGroups.Remove(NormDir(newDir));
                _hiddenGroups.Remove(NormDir(newDir));
            }

            SaveConfig();
            LoadGallery();
            SetStatus(wasGroup
                ? $"เปลี่ยนกลุ่มเป็นโฟลเดอร์: {newName} (เอาป้ายกำกับออกแล้ว)"
                : $"เปลี่ยนชื่อโฟลเดอร์เป็น: {newName}");
        }

        // ย้ายทุก state ที่อ้าง path เดิม (รวม path ลูก) ไปยัง path ใหม่หลังเปลี่ยนชื่อโฟลเดอร์
        private void RemapDirState(string oldDir, string newDir)
        {
            var o = NormDir(oldDir);
            var n = NormDir(newDir);
            string Remap(string p)
            {
                var np = NormDir(p);
                if (np.Equals(o, StringComparison.OrdinalIgnoreCase)) return n;
                if (np.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return n + np.Substring(o.Length);
                return p;
            }

            foreach (var set in new[] { _collapsedGroups, _hiddenGroups, _expandedDirs })
            {
                var remapped = set.Select(Remap).ToList();
                set.Clear();
                foreach (var p in remapped) set.Add(p);
            }
            foreach (var k in _activeGroups.Keys.ToList()) _activeGroups[k] = Remap(_activeGroups[k]);
            foreach (var k in _currentDirs.Keys.ToList()) _currentDirs[k] = Remap(_currentDirs[k]);
            if (!string.IsNullOrEmpty(_activeGroup)) _activeGroup = Remap(_activeGroup!);
            if (!string.IsNullOrEmpty(_currentDir)) _currentDir = Remap(_currentDir);
        }

        // สร้างโฟลเดอร์ใหม่ในโฟลเดอร์ที่ระบุ (คลิกขวาใน tree)
        private void CreateNewFolder(string parentDir)
        {
            var dlg = new InputDialog("สร้างโฟลเดอร์ใหม่", "ชื่อโฟลเดอร์:", "") { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Answer)) return;

            var name = dlg.Answer.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            { SetStatus("ชื่อโฟลเดอร์มีอักขระที่ใช้ไม่ได้", false); return; }
            if (name.StartsWith("grp_", StringComparison.OrdinalIgnoreCase))
            { SetStatus("ชื่อขึ้นต้นด้วย grp_ สงวนไว้สำหรับกลุ่มป้ายกำกับ", false); return; }

            var dir = Path.Combine(parentDir, name);
            if (Directory.Exists(dir)) { SetStatus("มีโฟลเดอร์ชื่อนี้อยู่แล้ว", false); return; }
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { SetStatus("สร้างโฟลเดอร์ไม่สำเร็จ: " + ex.Message, false); return; }

            // ขยาย node แม่ใน tree ให้เห็นโฟลเดอร์ใหม่ทันที
            _expandedDirs.Add(NormDir(parentDir));
            LoadGallery();
            SetStatus($"สร้างโฟลเดอร์: {name}");
        }

        // ── Gallery ─────────────────────────────────────────────────
        private void LoadGallery()
        {
            _galleryGeneration++;
            GalleryItems.Items.Clear();
            _cardToFile.Clear();
            _groupHeaders.Clear();
            _groupBadges.Clear();

            if (!Directory.Exists(_saveDir)) return;

            var viewDir = CurrentViewDir();
            if (SameDir(viewDir, _saveDir)) MigrateAlbumIfNeeded(_saveDir);

            PathLabel.Text = viewDir;
            BuildFolderTree();

            // รูปที่ยังไม่จัดกลุ่ม (อยู่ในรากของโฟลเดอร์ที่เปิดอยู่)
            var ungrouped = SortFiles(Directory.GetFiles(viewDir).Where(IsImage));

            // ล้างรายการกลุ่มที่ซ่อนซึ่ง folder ถูกลบไปแล้ว
            _hiddenGroups.RemoveWhere(d => !Directory.Exists(d));

            // โฟลเดอร์ย่อยปกติ (ไม่ใช่กลุ่ม grp_*) — แสดงเป็นการ์ดโฟลเดอร์แบบ Explorer
            var folderDirs = Directory.GetDirectories(viewDir)
                .Where(d => !IsGroupDir(d))
                .OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // โฟลเดอร์กลุ่ม (grp_*) เรียงตามชื่อ = เรียงตามเวลาสร้าง
            var allGroupDirs = Directory.GetDirectories(viewDir)
                .Where(IsGroupDir)
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_sortNewestFirst) allGroupDirs.Reverse();

            // กลุ่มที่ถูกซ่อน (คง folder จริงไว้) — ตัดออกจากการแสดงผล เว้นแต่กดแสดงชั่วคราว
            int hiddenCount = allGroupDirs.Count(d => _hiddenGroups.Contains(d));
            var groupDirs = _showHiddenGroups
                ? allGroupDirs
                : allGroupDirs.Where(d => !_hiddenGroups.Contains(d)).ToList();
            UpdateHiddenToggle(hiddenCount);

            // validate active group
            if (!string.IsNullOrEmpty(_activeGroup) && !Directory.Exists(_activeGroup))
                _activeGroup = null;

            // นับรูปในแต่ละกลุ่ม + รวมรูปทั้งหมด (ไม่นับป้าย)
            var groupImages = new Dictionary<string, List<string>>();
            int totalImages = ungrouped.Count;
            foreach (var g in groupDirs)
            {
                var imgs = SortFiles(Directory.GetFiles(g).Where(f => IsImage(f) && !IsLabelFile(f)));
                groupImages[g] = imgs;
                totalImages += imgs.Count;
            }
            CountLabel.Text = folderDirs.Count > 0
                ? $"{totalImages} รูป • {folderDirs.Count} โฟลเดอร์"
                : $"{totalImages} รูป";

            // ล้าง selection ที่ไม่มีอยู่แล้ว (เฉพาะไฟล์รูป)
            var valid = new HashSet<string>(ungrouped);
            foreach (var kv in groupImages) foreach (var f in kv.Value) valid.Add(f);
            _selected.IntersectWith(valid);

            var parentGrid = (Grid)GalleryItems.Parent;
            if (totalImages == 0 && allGroupDirs.Count == 0)
            {
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
                if (parentGrid.Children.Count > 2)
                    parentGrid.Children.RemoveAt(2);
                parentGrid.Children.Add(empty);
                return;
            }

            GalleryItems.Visibility = Visibility.Visible;
            while (parentGrid.Children.Count > 2)
                parentGrid.Children.RemoveAt(2);

            var cardEntries = new List<(Border card, Image img, string filepath)>();

            void AddUngrouped()
            {
                foreach (var fp in ungrouped)
                {
                    var (card, img) = CreateCard(fp);
                    GalleryItems.Items.Add(card);
                    cardEntries.Add((card, img, fp));
                }
            }

            void AddGroups()
            {
                foreach (var g in groupDirs)
                {
                    var (hcard, himg) = CreateGroupHeader(g, groupImages[g].Count);
                    GalleryItems.Items.Add(hcard);
                    var labelPath = Path.Combine(g, "_label.png");
                    if (File.Exists(labelPath))
                        cardEntries.Add((hcard, himg, labelPath));

                    if (!_collapsedGroups.Contains(g))
                    {
                        foreach (var fp in groupImages[g])
                        {
                            var (card, img) = CreateCard(fp);
                            GalleryItems.Items.Add(card);
                            cardEntries.Add((card, img, fp));
                        }
                    }
                }
            }

            // newest-first: กลุ่มใหม่บนสุด, "ไม่จัดกลุ่ม" (เก่าสุด) ล่างสุด — และกลับกันเมื่อ oldest-first
            if (_sortNewestFirst) { AddGroups(); AddUngrouped(); }
            else { AddUngrouped(); AddGroups(); }

            UpdateSelBar();

            var gen = _galleryGeneration;
            _ = LoadThumbnailsAsync(cardEntries, gen);

            if (!_sortNewestFirst)
                Dispatcher.InvokeAsync(() => GalleryScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
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
                            // ป้ายกำกับถอดรหัสที่ความละเอียดสูงขึ้น เพื่อให้ตัวอักษรคมชัด (แสดงกว้างกว่า + อาจสูง)
                            bi.DecodePixelWidth = IsLabelFile(filepath) ? (ThumbSize + 20) * 2 : ThumbSize;
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
            // Label files: show as full-width banner
            if (IsLabelFile(filepath))
                return CreateLabelCard(filepath);

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

            // Copy path button (overlay, hidden by default)
            var copyBtn = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 120, 215)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                ToolTip = "Copy Path",
                Child = new TextBlock
                {
                    Text = "\U0001F4CB",
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var fpCopy = filepath;
            copyBtn.MouseLeftButtonDown += (_, e) =>
            {
                Clipboard.SetText(fpCopy);
                SetStatus("คัดลอกแล้ว: " + Path.GetFileName(fpCopy));
                e.Handled = true;
            };

            var thumbGrid = new Grid
            {
                Width = ThumbSize,
                Height = ThumbSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };

            var imgBorder = new Border
            {
                BorderBrush = new SolidColorBrush(_isDark
                    ? Color.FromRgb(60, 60, 80)
                    : Color.FromRgb(200, 200, 200)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Padding = new Thickness(4),
                Child = img
            };

            thumbGrid.Children.Add(imgBorder);
            thumbGrid.Children.Add(copyBtn);

            // Show/hide copy button on hover
            thumbGrid.MouseEnter += (_, _) => copyBtn.Visibility = Visibility.Visible;
            thumbGrid.MouseLeave += (_, _) => copyBtn.Visibility = Visibility.Collapsed;

            stack.Children.Add(thumbGrid);

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

        private (Border card, Image img) CreateLabelCard(string filepath)
        {
            var isSelected = _selected.Contains(filepath);
            var selectedBg = new SolidColorBrush(_isDark
                ? Color.FromArgb(60, 99, 102, 241)
                : Color.FromArgb(40, 0, 120, 215));
            var selectedBorder = new SolidColorBrush(_isDark
                ? Color.FromArgb(120, 99, 102, 241)
                : Color.FromArgb(100, 0, 120, 215));

            var accentColor = new SolidColorBrush(Color.FromRgb(99, 102, 241));

            // Extract label text from filename: _label_20260517_120000.png → parse from image
            // We show the thumbnail image which already has the text rendered
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                // ล็อกแค่ความกว้าง ปล่อยให้ความสูงเลื่อนตามสัดส่วนจริงของรูปป้าย
                // ป้ายข้อความยาว (รูปสูง) จะได้ thumbnail สูงตาม แสดงตัวอักษรใหญ่สุดเท่าที่แสดงได้
                Width = ThumbSize + 20,
                MaxHeight = 200,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var card = new Border
            {
                Background = isSelected ? selectedBg : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                BorderBrush = isSelected ? selectedBorder : Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(4),
                Margin = new Thickness(2, 6, 2, 6),
                Width = 170,
                Cursor = Cursors.Hand
            };

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

            // Label icon indicator
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 4) };
            headerPanel.Children.Add(new TextBlock
            {
                Text = "\U0001F3F7\uFE0F",
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = "ป้ายกำกับ",
                Foreground = accentColor,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(headerPanel);

            // Thumbnail showing the rendered label image — สูงตามสัดส่วนรูปอัตโนมัติ
            var imgBorder = new Border
            {
                BorderBrush = accentColor,
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(25, 25, 50)),
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
                Child = img
            };
            stack.Children.Add(imgBorder);

            card.Child = stack;
            _cardToFile[card] = filepath;
            return (card, img);
        }

        // ── Group header (ป้ายกำกับ = หัวกลุ่ม พร้อมยุบ/ขยาย + active) ─
        private (Border card, Image img) CreateGroupHeader(string groupDir, int imgCount)
        {
            var labelPath = Path.Combine(groupDir, "_label.png");
            bool isActive = groupDir == _activeGroup;
            bool collapsed = _collapsedGroups.Contains(groupDir);
            bool hidden = _hiddenGroups.Contains(groupDir);
            var accentBrush = new SolidColorBrush(Color.FromRgb(99, 102, 241));

            var card = new Border
            {
                Background = isActive ? new SolidColorBrush(Color.FromArgb((byte)(_isDark ? 40 : 30), 99, 102, 241)) : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                BorderBrush = isActive ? accentBrush : Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(4),
                Margin = new Thickness(2, 6, 2, 6),
                Width = 170,
                Opacity = hidden ? 0.5 : 1.0,
                Cursor = Cursors.Hand,
                ToolTip = "คลิกเพื่อบันทึกรูปใหม่ลงกลุ่มนี้ • ดับเบิลคลิกเพื่อดูป้าย"
            };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

            // แถวบน: [▾ ยุบ/ขยาย] [🏷️ ป้ายกำกับ (n)] [🙈 ซ่อน]
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chevron = new TextBlock
            {
                Text = collapsed ? "▸" : "▾",
                FontSize = 14,
                Foreground = accentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 4, 0),
                Cursor = Cursors.Hand,
                ToolTip = collapsed ? "ขยายกลุ่ม" : "ยุบกลุ่ม"
            };
            chevron.MouseLeftButtonDown += (_, e) => { e.Handled = true; ToggleCollapse(groupDir); };
            Grid.SetColumn(chevron, 0);
            top.Children.Add(chevron);

            var titlePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            titlePanel.Children.Add(new TextBlock
            {
                Text = "\U0001F3F7️",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            titlePanel.Children.Add(new TextBlock
            {
                Text = $"ป้ายกำกับ ({imgCount})",
                Foreground = accentBrush,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(titlePanel, 1);
            top.Children.Add(titlePanel);

            // ปุ่มซ่อน/ยกเลิกซ่อน — คง folder จริงไว้เสมอ
            var hideBtn = new TextBlock
            {
                Text = hidden ? "\U0001F441" : "\U0001F648",   // 👁 = แสดงอีกครั้ง, 🙈 = ซ่อน
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = hidden ? "ยกเลิกการซ่อนกลุ่มนี้" : "ซ่อนกลุ่มนี้ (ไม่ลบ folder จริง)"
            };
            hideBtn.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (hidden) UnhideGroup(groupDir); else HideGroup(groupDir);
            };
            Grid.SetColumn(hideBtn, 2);
            top.Children.Add(hideBtn);

            stack.Children.Add(top);

            // ป้ายบอกกลุ่มที่กำลังบันทึกลง
            var badge = new TextBlock
            {
                Text = "● บันทึกรูปใหม่ที่นี่",
                FontSize = 10,
                Foreground = accentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                Visibility = isActive ? Visibility.Visible : Visibility.Collapsed
            };
            stack.Children.Add(badge);
            _groupBadges[groupDir] = badge;

            // รูปป้าย (header identity) — คงแสดงเสมอแม้ยุบกลุ่ม
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                Width = ThumbSize + 20,
                MaxHeight = 200,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var imgBorder = new Border
            {
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(25, 25, 50)),
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                Child = img
            };
            stack.Children.Add(imgBorder);

            card.Child = stack;

            // คลิก body = ตั้งเป็นกลุ่ม active, ดับเบิลคลิก = พรีวิวป้าย
            card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    if (File.Exists(labelPath)) ShowPreview(labelPath);
                }
                else SetActiveGroup(groupDir);
                e.Handled = true;
            };

            // เมนูคลิกขวา — เปลี่ยนกลุ่มให้เป็นโฟลเดอร์ชื่อจริง (เอาป้ายออก) ได้จากตรงนี้เลย
            var menu = new ContextMenu();
            var miConvert = new MenuItem { Header = "เปลี่ยนเป็นโฟลเดอร์ (ตั้งชื่อแทนป้าย)..." };
            miConvert.Click += (_, _) => RenameFolder(groupDir);
            menu.Items.Add(miConvert);
            var miOpen = new MenuItem { Header = "เปิดใน Explorer" };
            miOpen.Click += (_, _) => Process.Start("explorer.exe", groupDir);
            menu.Items.Add(miOpen);
            card.ContextMenu = menu;

            _groupHeaders[groupDir] = card;
            return (card, img);
        }

        private void ToggleCollapse(string groupDir)
        {
            if (!_collapsedGroups.Remove(groupDir))
            {
                _collapsedGroups.Add(groupDir);
                // ยุบกลุ่มที่กำลัง active → เลิก active เพื่อไม่ให้รูปใหม่ลงโฟลเดอร์ที่ยุบอยู่
                if (_activeGroup == groupDir)
                {
                    _activeGroup = null;
                    _activeGroups.Remove(_currentAlbum);
                    SetStatus("ยุบกลุ่มแล้ว — รูปใหม่จะบันทึกลง Album (ไม่จัดกลุ่ม)");
                }
            }
            SaveConfig();
            LoadGallery();
        }

        private void SetActiveGroup(string groupDir)
        {
            if (_activeGroup == groupDir)
            {
                _activeGroup = null;
                _activeGroups.Remove(_currentAlbum);
                SetStatus("บันทึกลง Album (ไม่จัดกลุ่ม)");
            }
            else if (_collapsedGroups.Contains(groupDir))
            {
                SetStatus("กลุ่มนี้ถูกยุบอยู่ — ขยายกลุ่มก่อนจึงจะบันทึกรูปใหม่ลงได้", false);
                return;
            }
            else
            {
                _activeGroup = groupDir;
                _activeGroups[_currentAlbum] = groupDir;
                SetStatus("บันทึกรูปใหม่ลงกลุ่มนี้");
            }
            SaveConfig();
            RefreshActiveStyles();
        }

        private void RefreshActiveStyles()
        {
            var accentBrush = new SolidColorBrush(Color.FromRgb(99, 102, 241));
            foreach (var kv in _groupHeaders)
            {
                bool act = kv.Key == _activeGroup;
                kv.Value.BorderBrush = act ? accentBrush : Brushes.Transparent;
                kv.Value.Background = act
                    ? new SolidColorBrush(Color.FromArgb((byte)(_isDark ? 40 : 30), 99, 102, 241))
                    : Brushes.Transparent;
                if (_groupBadges.TryGetValue(kv.Key, out var b))
                    b.Visibility = act ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ── ซ่อน/ยกเลิกซ่อนกลุ่ม (ไม่ลบ folder จริง) ─────────────────
        private void HideGroup(string groupDir)
        {
            _hiddenGroups.Add(groupDir);
            if (_activeGroup == groupDir)
            {
                _activeGroup = null;
                _activeGroups.Remove(_currentAlbum);
            }
            SaveConfig();
            _selected.Clear();
            LoadGallery();
            SetStatus("ซ่อนกลุ่มแล้ว (folder จริงยังอยู่)");
        }

        private void UnhideGroup(string groupDir)
        {
            _hiddenGroups.Remove(groupDir);
            SaveConfig();
            LoadGallery();
            SetStatus("ยกเลิกการซ่อนกลุ่มแล้ว");
        }

        private void UpdateHiddenToggle(int hiddenCount)
        {
            if (HiddenToggleBtn == null) return;
            if (hiddenCount == 0)
            {
                _showHiddenGroups = false;
                HiddenToggleBtn.Visibility = Visibility.Collapsed;
                return;
            }
            HiddenToggleBtn.Visibility = Visibility.Visible;
            HiddenToggleBtn.Content = _showHiddenGroups ? "ซ่อนอีกครั้ง" : $"กลุ่มที่ซ่อน ({hiddenCount})";
        }

        private void HiddenToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            _showHiddenGroups = !_showHiddenGroups;
            LoadGallery();
        }

        // ── Selection ────────────────────────────���──────────────────
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

            // Copy in the same order as current gallery display
            var sorted = _sortNewestFirst
                ? _selected.OrderByDescending(f => File.GetLastWriteTime(f)).ToList()
                : _selected.OrderBy(f => File.GetLastWriteTime(f)).ToList();
            var paths = string.Join("\n", sorted);
            Clipboard.SetText(paths);
            var order = _sortNewestFirst ? "ใหม่→เก่า" : "เก่า→ใหม่";
            SetStatus($"คัดลอก {_selected.Count} path ({order})");
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
                _selected.Clear();
                _selected.Add(fp);
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

        // ── Win32 API for real screen size (physical pixels) ────
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        private static BitmapSource? CaptureScreen()
        {
            // Use Win32 GetSystemMetrics — always returns physical pixels
            var screenLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var screenTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var screenWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var screenHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

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

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        // ── Capture Window ──────────────────────────────────────────
        private void CaptureWindowBtn_Click(object sender, RoutedEventArgs e)
        {
            CaptureWindow();
        }

        private void CaptureWindow()
        {
            Directory.CreateDirectory(_saveDir);

            try
            {
                var ownerHwnd = new WindowInteropHelper(this).Handle;
                var picker = new WindowPickerWindow(ownerHwnd) { Owner = this };

                if (picker.ShowDialog() == true)
                {
                    var captured = picker.CaptureSelectedWindow();
                    if (captured == null)
                    {
                        SetStatus("Capture failed — หน้าต่างอาจถูกปิดแล้ว", false);
                        return;
                    }

                    var fp = SaveImage(captured);
                    _selected.Clear();
                    _selected.Add(fp);
                    LoadGallery();
                    Clipboard.SetText(fp);
                    SetStatus($"บันทึก + คัดลอก path: {picker.SelectedTitle}");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}", false);
            }
        }

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
                    _selected.Clear();
                    _selected.Add(fp);
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
            var dir = CurrentSaveDir();
            Directory.CreateDirectory(dir);
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    var fp = SaveImage(img);
                    _selected.Clear();
                    _selected.Add(fp);
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
                            var dest = UniquePath(Path.Combine(dir, Path.GetFileName(f)));
                            File.Copy(f, dest);
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
                        var dest = UniquePath(Path.Combine(dir, Path.GetFileName(path)));
                        File.Copy(path, dest);
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
            var dir = CurrentSaveDir();
            Directory.CreateDirectory(dir);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fp = UniquePath(Path.Combine(dir, $"screenshot_{ts}.png"));

            using var fs = new FileStream(fp, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            encoder.Save(fs);

            return fp;
        }

        // ── Sort Toggle ─────────────────────────────────────────────
        private void SortToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            _sortNewestFirst = !_sortNewestFirst;
            UpdateSortButton();
            LoadGallery();

            // Auto-scroll to bottom when old→new (latest at bottom)
            if (!_sortNewestFirst)
                Dispatcher.InvokeAsync(() => GalleryScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void UpdateSortButton()
        {
            if (SortToggleBtn != null)
                SortToggleBtn.Content = _sortNewestFirst ? "ใหม่→เก่า" : "เก่า→ใหม่";
        }

        private static bool IsLabelFile(string filepath)
        {
            var name = Path.GetFileName(filepath);
            // ใหม่: screenshot_<ts>_label.png  |  เก่า: _label_<ts>.png (รองรับไว้กันไฟล์เดิมพัง)
            return name.EndsWith("_label.png", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("_label_", StringComparison.OrdinalIgnoreCase);
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
            var fg = new SolidColorBrush(_isDark ? Color.FromRgb(230, 230, 230) : Color.FromRgb(26, 26, 46));
            var fgSub = new SolidColorBrush(_isDark ? Color.FromRgb(160, 160, 160) : Color.FromRgb(85, 85, 85));

            if (_isDark)
            {
                var bg1 = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                var bg2 = new SolidColorBrush(Color.FromRgb(39, 39, 39));
                var bg3 = new SolidColorBrush(Color.FromRgb(25, 25, 25));

                MainWin.Background = bg3;
                ToolbarBorder.Background = bg2;
                ToolbarBorder.BorderBrush = borderBrush;
                AddressBar.Background = bg1;
                AddressBar.BorderBrush = borderBrush;
                SidebarBorder.Background = bg3;
                SidebarBorder.BorderBrush = borderBrush;
                TreeSplitter.Background = borderBrush;
                GalleryBorder.Background = bg3;
                BottomBar.Background = bg1;
                BottomBar.BorderBrush = borderBrush;
                ThemeBtn.Content = "\u2600";
            }
            else
            {
                MainWin.Background = Brushes.White;
                ToolbarBorder.Background = new SolidColorBrush(Color.FromRgb(249, 249, 249));
                ToolbarBorder.BorderBrush = borderBrush;
                AddressBar.Background = Brushes.White;
                AddressBar.BorderBrush = borderBrush;
                SidebarBorder.Background = Brushes.White;
                SidebarBorder.BorderBrush = borderBrush;
                TreeSplitter.Background = borderBrush;
                GalleryBorder.Background = Brushes.White;
                BottomBar.Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));
                BottomBar.BorderBrush = borderBrush;
                ThemeBtn.Content = "\u263D";
            }

            // Update all toolbar buttons foreground
            ApplyForegroundToToolbar(ToolbarBorder, fg);

            // Status bar
            SelectAllCheck.Foreground = fg;
            PathLabel.Foreground = fgSub;
            TreeCountLabel.Foreground = fgSub;
            CountLabel.Foreground = fgSub;
            SelInfoLabel.Foreground = fgSub;

            // Separators
            foreach (var child in LogicalTreeHelper.GetChildren(ToolbarBorder))
            {
                if (child is StackPanel sp)
                {
                    foreach (var item in sp.Children)
                    {
                        if (item is Border b && b.Width == 1) // separator
                            b.Background = new SolidColorBrush(_isDark
                                ? Color.FromArgb(48, 255, 255, 255)
                                : Color.FromArgb(48, 0, 0, 0));
                    }
                }
            }

            RefreshAlbumTabs();
        }

        private void ApplyForegroundToToolbar(DependencyObject parent, SolidColorBrush fg)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is Button btn)
                    btn.Foreground = fg;
                if (child is TextBlock tb && tb.Name != "StatusLabel")
                    tb.Foreground = fg;
                if (child is DependencyObject d)
                    ApplyForegroundToToolbar(d, fg);
            }
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
            // ระหว่างพิมพ์ค้นหา album — อย่าให้คีย์ลัดของหน้าต่างทำงาน (เช่น Ctrl+V paste รูป)
            if (AlbumSearchBox.IsKeyboardFocusWithin)
                return;

            switch (e.Key)
            {
                case Key.F5:
                    await CaptureFullScreen();
                    break;
                case Key.F6:
                    CaptureWindow();
                    break;
                case Key.Delete:
                    DeleteSelBtn_Click(sender, e);
                    break;
                case Key.V when Keyboard.Modifiers == ModifierKeys.Control:
                    PasteClipboard();
                    break;
                case Key.R when Keyboard.Modifiers == ModifierKeys.Control:
                    RefreshBtn_Click(sender, e);
                    break;
                case Key.K when Keyboard.Modifiers == ModifierKeys.Control:
                    OpenAlbumPopup();
                    break;
                case Key.Back:
                    NavigateUp();
                    break;
                case Key.Escape:
                    if (AlbumPopup.IsOpen)
                        AlbumPopup.IsOpen = false;
                    else
                        Close();
                    break;
            }
        }
    }
}
