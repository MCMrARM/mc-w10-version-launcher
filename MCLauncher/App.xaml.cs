using System.Diagnostics;
using System.Windows;
using Windows.Management.Deployment;

namespace MCLauncher {
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application {

        protected void App_Startup(object sender, StartupEventArgs e) {
            MainWindow mainWindow = new MainWindow();

            if (e.Args.Length == 2 && e.Args[0] == "/Uninstall") {
                foreach (var pkg in new PackageManager().FindPackages(e.Args[1])) {
                    mainWindow.RemovePackage(pkg, e.Args[1]).RunSynchronously();
                }

                Shutdown();
                return;
            }
            mainWindow.Show();

            Debug.Listeners.Add(new TextWriterTraceListener("Log.txt"));
            Debug.AutoFlush = true;
        }

    }
}
