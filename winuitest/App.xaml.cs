using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winuitest
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        // Allows development validation in a separate profile without changing
        // the listener's active account, queue or saved playback position.
        internal static string? DataDirectory
        {
            get
            {
                var path = Environment.GetEnvironmentVariable("LUMA_DATA_DIRECTORY");
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
        }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            UnhandledException += (_, e) => WriteStartupDiagnostic(e.Exception);
            InitializeComponent();
        }

        private static void WriteStartupDiagnostic(Exception error)
        {
            // Opt-in development diagnostics; never record music responses or account credentials.
            var path = Environment.GetEnvironmentVariable("LUMA_STARTUP_DIAGNOSTIC");
            if (string.IsNullOrWhiteSpace(path)) return;
            try { File.WriteAllText(path, error.ToString()); } catch { }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                _window = new MainWindow();
                _window.Activate();
            }
            catch (Exception error) { WriteStartupDiagnostic(error); throw; }
        }
    }
}
