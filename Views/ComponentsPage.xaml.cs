using CommunityToolkit.WinUI.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PickandPlace2026.Classes;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PickandPlace2026.Views
{
    public sealed partial class ComponentsPage : Page
    {
        // Shared App.comp instance, not a page-local Components() - see the comment
        // on PCBBuilder.comp for why a separate instance here would go stale.
        private Components _comp => ((App)Application.Current).comp;
        private ObservableCollection<Component> _components = new();

        public ComponentsPage()
        {
            InitializeComponent();
            SetupGrid(dg_data);
            LoadComponents();
        }

        private void SetStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            ((App)Application.Current).ReportStatus(message, severity);
        }

        private void LoadComponents()
        {
            _components = new ObservableCollection<Component>(_comp.LoadComponents());
            dg_data.ItemsSource = _components;
        }

        // Columns bind against Component's real CLR properties now, rather than
        // DataRowView's string indexer (see the old version of this file for why
        // that indexer trick was needed for a DataView-backed grid).
        private static void SetupGrid(DataGrid dg)
        {
            dg.AutoGenerateColumns = false;
            AddTextColumn(dg, "Code", "ComponentCode");
            AddTextColumn(dg, "Value", "ComponentValue");
            AddTextColumn(dg, "Package", "Package");
            AddTextColumn(dg, "Placement Height", "PlacementHeight");
            AddTextColumn(dg, "Feeder Height", "FeederHeight");
            AddTextColumn(dg, "Feeder X", "FeederX");
            AddTextColumn(dg, "Feeder Y", "FeederY");
            AddTextColumn(dg, "Nozzle", "PickerNozzle");
            dg.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Verify w/ Camera",
                Binding = new Binding { Path = new PropertyPath("VerifywithCamera") },
                IsReadOnly = false
            });
            dg.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Tape Feeder",
                Binding = new Binding { Path = new PropertyPath("TapeFeeder") },
                IsReadOnly = false
            });
            AddTextColumn(dg, "Feeder ID", "FeederID");
            AddTextColumn(dg, "Place Speed", "PlaceSpeed");
        }

        private static void AddTextColumn(DataGrid dg, string header, string binding)
        {
            dg.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding { Path = new PropertyPath(binding) },
                IsReadOnly = false
            });
        }

        private void bt_Save_Click(object sender, RoutedEventArgs e)
        {
            _comp.SaveComponents(new List<Component>(_components));
            SetStatus("Saved changes", InfoBarSeverity.Success);
        }

        private void bt_Load_Click(object sender, RoutedEventArgs e)
        {
            LoadComponents();
            SetStatus("Reloaded from disk");
        }

        private void dg_data_CellEditEnded(object sender, DataGridCellEditEndedEventArgs e)
        {
        }
    }
}
