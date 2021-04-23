﻿using System.Diagnostics;
using System.Windows;

namespace MCLauncher {
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application {

        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);
            Debug.Listeners.Add(new TextWriterTraceListener("Log.txt"));
            Debug.AutoFlush = true;
        }

    }
}
