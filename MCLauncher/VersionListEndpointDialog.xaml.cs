using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MCLauncher
{
    /// <summary>
    /// Interaction logic for VersionListEndpointDialog.xaml
    /// </summary>
    public partial class VersionListEndpointDialog : Window
    {
        public event SetEndpointHandler OnEndpointChanged;

        public delegate void SetEndpointHandler(object sender, string newEndpoint);


        public VersionListEndpointDialog(string currentEndpoint) {
            InitializeComponent();
            EndpointTextBox.Text = currentEndpoint;
        }

        private void okButton_Click(object sender, RoutedEventArgs e) {
            OnEndpointChanged?.Invoke(this, EndpointTextBox.Text);
            Close();
        }

        private void cancelButton_Click(object sender, RoutedEventArgs e) {
            Close();
        }
    }
}
