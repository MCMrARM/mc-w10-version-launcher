using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using Windows.Management.Deployment;

namespace MCLauncher {
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application {

        protected void App_Startup(object sender, StartupEventArgs e) {
            MainWindow mainWindow = new MainWindow();

            if (e.Args.Contains("/Uninstall")) {
                //Debugger.Launch();

                foreach (var pkg in new PackageManager().FindPackages(MinecraftPackageFamilies.MINECRAFT)) {
                    mainWindow.RemovePackage(pkg, MinecraftPackageFamilies.MINECRAFT).RunSynchronously();
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
