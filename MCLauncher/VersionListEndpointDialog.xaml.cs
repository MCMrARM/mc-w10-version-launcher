using System.Windows;

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
