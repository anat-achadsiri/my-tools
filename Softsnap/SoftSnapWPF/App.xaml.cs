using System.Runtime.InteropServices;
using System.Windows;

namespace SoftSnapWPF
{
    public partial class App : Application
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        protected override void OnStartup(StartupEventArgs e)
        {
            SetProcessDPIAware();
            base.OnStartup(e);
        }
    }
}
