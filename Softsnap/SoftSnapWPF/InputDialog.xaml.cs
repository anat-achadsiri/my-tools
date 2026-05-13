using System.Windows;

namespace SoftSnapWPF
{
    public partial class InputDialog : Window
    {
        public string Answer => InputBox.Text;

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptLabel.Text = prompt;
            InputBox.Text = defaultValue;
            InputBox.SelectAll();
            InputBox.Focus();
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
