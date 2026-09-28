using System.Windows.Controls;

namespace ImageRotater
{
    // Keep code-behind limited to view-only navigation. Settings state, tool
    // detection and validation remain owned by the view model.
    public partial class ImageRotaterSettingsView : UserControl
    {
        public ImageRotaterSettingsView()
        {
            InitializeComponent();
        }

        private void OpenBackgrounds_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MainSettingsTabs.SelectedItem = BackgroundsTab;
        }

        private void OpenCovers_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MainSettingsTabs.SelectedItem = CoversTab;
        }

        private void OpenTools_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MainSettingsTabs.SelectedItem = ToolsTab;
        }
    }
}
