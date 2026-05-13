using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoftSnapWPF
{
    public partial class PreviewWindow : Window
    {
        private readonly string _filepath;
        public event Action? FileDeleted;

        // ICommand for ESC KeyBinding
        public ICommand CloseCommand { get; }

        public PreviewWindow(string filepath, bool isDark)
        {
            CloseCommand = new RelayCommand(_ => Close());

            InitializeComponent();
            _filepath = filepath;

            // Theme
            PreviewWin.Background = new SolidColorBrush(isDark
                ? Color.FromRgb(12, 10, 26)
                : Color.FromRgb(242, 242, 247));

            FileNameLabel.Text = Path.GetFileName(filepath);
            FullPathLabel.Text = filepath;

            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(filepath);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();

                PreviewImage.Source = bi;

                // Size window to image
                var sw = SystemParameters.PrimaryScreenWidth * 0.85;
                var sh = SystemParameters.PrimaryScreenHeight * 0.85;
                var ratio = Math.Min(sw / bi.PixelWidth, sh / bi.PixelHeight);
                ratio = Math.Min(ratio, 1.0);

                Width = bi.PixelWidth * ratio + 40;
                Height = bi.PixelHeight * ratio + 120;
            }
            catch (Exception ex)
            {
                PreviewImage.Visibility = Visibility.Collapsed;
                FileNameLabel.Text = $"Error: {ex.Message}";
            }
        }

        private void CopyPathBtn_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(_filepath);
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
