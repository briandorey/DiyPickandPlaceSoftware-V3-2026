using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PickandPlace2026.Classes;

namespace PickandPlace2026.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly App _app = (App)Application.Current;

        public SettingsPage()
        {
            InitializeComponent();
            PopulateFields(_app.Settings);
        }

        private void PopulateFields(AppSettings s)
        {
            num_Nozzle1X.Value = s.Nozzle1Xoffset;
            num_Nozzle1Y.Value = s.Nozzle1Yoffset;
            num_Nozzle2X.Value = s.Nozzle2Xoffset;
            num_Nozzle2Y.Value = s.Nozzle2Yoffset;
            num_ClearHeight.Value = s.ClearHeight;
            num_PickSpeed.Value = s.PickSpeed;
        }

        private void bt_Save_Click(object sender, RoutedEventArgs e)
        {
            AppSettings s = _app.Settings;
            s.Nozzle1Xoffset = num_Nozzle1X.Value;
            s.Nozzle1Yoffset = num_Nozzle1Y.Value;
            s.Nozzle2Xoffset = num_Nozzle2X.Value;
            s.Nozzle2Yoffset = num_Nozzle2Y.Value;
            s.ClearHeight = num_ClearHeight.Value;
            s.PickSpeed = num_PickSpeed.Value;
            s.Save();

            _app.ReportStatus("Settings saved", InfoBarSeverity.Success);
        }

        // Loads default values into the fields without touching the persisted
        // settings - nothing is overwritten until Save is clicked.
        private void bt_ResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            PopulateFields(new AppSettings());
            _app.ReportStatus("Defaults loaded - click Save to persist", InfoBarSeverity.Informational);
        }
    }
}
