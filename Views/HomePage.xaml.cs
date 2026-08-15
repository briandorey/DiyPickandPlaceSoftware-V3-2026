using CommunityToolkit.WinUI.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using PickandPlace2026.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PickandPlace2026.Views
{
    public sealed partial class HomePage : Page
    {
        private readonly DataTable dtLog = new DataTable();
        private readonly string _logFile;

        private Board? _board;
        private ObservableCollection<BoardComponent> _components = new();

        private readonly Components _comp = new Components();
        private readonly App _app = (App)Application.Current;
        private UsbDevice _usbController;
        private readonly Kflop _kflop;

        public HomePage()
        {
            InitializeComponent();

            _kflop = _app.GetKFlop();
            _usbController = _app.GetUSBDevice();
            _app.HardwareStatusChanged += (s, message) =>
            {
                if (message == "Controller Started")
                    _usbController = _app.GetUSBDevice();
            };
            _app.pcbBuilder.ErrorOccurred += (s, message) => SetStatus(message, InfoBarSeverity.Error);
            _app.pcbBuilder.BuildProgress += (s, message) => SetStatus(message);

            string saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\\pickandplacelogs\\";
            System.IO.Directory.CreateDirectory(saveDir);
            _logFile = saveDir + DateTime.Now.ToString("yyyyMMdd-HH-mm") + ".xml";

            dtLog.TableName = "boardcomponents";
            dtLog.Columns.Add("ComponentValue", typeof(string));
            dtLog.Columns.Add("Package", typeof(string));
            dtLog.Columns.Add("TapeFeeder", typeof(bool));
            dtLog.Columns.Add("FeederID", typeof(string));
            dtLog.Columns.Add("ComponentCode", typeof(int));
            dtLog.Columns.Add("Placed", typeof(int));
            dtLog.WriteXml(_logFile);

            SetupComponentsGrid(_dgComponents);
            SetupFeedersGrid(_dgFeeders);
        }

        private void SetStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            _app.ReportStatus(message, severity);
        }

        // _dgComponents binds to an ObservableCollection<BoardComponent> - real CLR
        // properties, so plain property-name paths work directly.
        private void SetupComponentsGrid(DataGrid dg)
        {
            dg.AutoGenerateColumns = false;
            AddColumn(dg, "RefDes", "ComponentName");
            AddColumn(dg, "PosX", "PlacementX");
            AddColumn(dg, "PosY", "PlacementY");
            AddColumn(dg, "Rotate", "PlacementRotate");
            AddColumn(dg, "Nozzle", "PlacementNozzle");
            dg.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Pick",
                Binding = new Binding { Path = new PropertyPath("Pick") },
                IsReadOnly = false
            });
        }

        private static void AddColumn(DataGrid dg, string header, string binding)
        {
            dg.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding { Path = new PropertyPath(binding) },
                IsReadOnly = false
            });
        }

        private void SetupFeedersGrid(DataGrid dg)
        {
            dg.AutoGenerateColumns = false;
            AddTextColumn(dg, "Feeder", "FeederID");
            AddTextColumn(dg, "Part", "ComponentValue");
            AddTextColumn(dg, "Package", "Package");
        }

        // _dgFeeders still binds to dtLog, a DataTable (see dtLog above) - its
        // ItemsSource is a DataView (DataRowView items). WinUI's Binding engine
        // doesn't support ICustomTypeDescriptor the way WPF does, so a plain
        // property-name path resolves to nothing and cells render blank - the
        // indexer syntax binds through DataRowView's string indexer instead,
        // which WinUI does support.
        private static void AddTextColumn(DataGrid dg, string header, string binding)
        {
            dg.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding { Path = new PropertyPath("[" + binding + "]") },
                IsReadOnly = false
            });
        }

        private async void Bt_Load_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker picker = new FileOpenPicker();
            IntPtr hwnd = WindowNative.GetWindowHandle(_app.GetMainWindow());
            InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                string json = File.ReadAllText(file.Path);
                Board? board = JsonSerializer.Deserialize(json, BoardJsonContext.Default.Board);

                if (board == null)
                {
                    SetStatus("Board file is empty or malformed.", InfoBarSeverity.Error);
                    return;
                }

                _board = board;
                _components = new ObservableCollection<BoardComponent>(_board.Components);

                ItemTitle.Text = _board.BoardInfo.BoardName;
                csfeeder.Text = _board.BoardInfo.MotorRunTime.ToString();

                // populate feeder log: for every feeder position (sorted by feeder
                // ID) that has at least one matching component on this board, log it
                dtLog.Clear();
                List<Component> masterComponents = _comp.LoadComponents();

                foreach (Component feederComponent in masterComponents.OrderBy(c => c.FeederID))
                {
                    if (_board.Components.Any(c => c.ComponentCode == feederComponent.ComponentCode))
                    {
                        dtLog.Rows.Add(feederComponent.ComponentValue, feederComponent.Package,
                            feederComponent.TapeFeeder.ToString(), feederComponent.FeederID,
                            feederComponent.ComponentCode, 0);
                    }
                }

                dtLog.WriteXml(_logFile);

                _dgFeeders.ItemsSource = dtLog.DefaultView;
                _dgComponents.ItemsSource = _components;

                // No camera preview control on this page anymore (it was never
                // actually wired to a capture device - see the removed XAML's
                // comment) - imgref is only used by CheckWithCamera, which
                // nothing in the current build loop calls.
                _app.pcbBuilder.SetupPCBBuilder(_kflop, _usbController, _board, _logFile, null, null);

                SetStatus($"Loaded {file.Name} ({_components.Count} components)", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                SetStatus("Failed to load board file: " + ex.Message, InfoBarSeverity.Error);
            }
        }

        private async void Bt_Save_Click(object sender, RoutedEventArgs e)
        {
            if (_board == null)
            {
                SetStatus("Load a board file first.", InfoBarSeverity.Error);
                return;
            }

            FileSavePicker picker = new FileSavePicker();
            IntPtr hwnd = WindowNative.GetWindowHandle(_app.GetMainWindow());
            InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("JSON file", new List<string> { ".json" });
            picker.SuggestedFileName = ItemTitle.Text;

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            _board.BoardInfo.MotorRunTime = int.Parse(csfeeder.Text);
            _board.Components = _components.ToList();

            string json = JsonSerializer.Serialize(_board, BoardJsonContext.Default.Board);
            File.WriteAllText(file.Path, json);

            SetStatus("File saved", InfoBarSeverity.Success);
        }

        private void Bt_Camera_Click(object sender, RoutedEventArgs e)
        {
            CameraWindow cam = new CameraWindow();
            cam.Activate();
        }

        private void Bt_HomeAll_Click(object sender, RoutedEventArgs e)
        {
            if (_usbController == null)
            {
                SetStatus("USB controller is not connected.", InfoBarSeverity.Error);
                return;
            }

            _usbController.SetResetFeeder();
            _kflop.HomeAll();
        }

        // internal: see Bt_Start_Click above.
        internal void Bt_ChipFeeder_Click(object sender, RoutedEventArgs? e)
        {
            if (_usbController == null)
            {
                SetStatus("USB controller is not connected.", InfoBarSeverity.Error);
                return;
            }

            _usbController.RunVibrationMotor(int.Parse(csfeeder.Text));
        }

        // internal: see Bt_Start_Click above.
        internal void Bt_CheckAll_Click(object sender, RoutedEventArgs? e)
        {
            SetPickAll(true);
        }

        // internal: see Bt_Start_Click above.
        internal void Bt_UnCheckAll_Click(object sender, RoutedEventArgs? e)
        {
            SetPickAll(false);
        }

        private void SetPickAll(bool pick)
        {
            if (_board == null) return;

            foreach (BoardComponent component in _components)
            {
                component.Pick = pick;
            }

            // Mutating a bound item's property directly doesn't raise anything
            // this DataGrid listens for - ObservableCollection<T> only notifies on
            // Add/Remove/collection-level changes, not property changes on items
            // it already holds. Re-assigning ItemsSource forces a rebind (same fix
            // as ComponentsPage/BoardDesigner).
            _dgComponents.ItemsSource = null;
            _dgComponents.ItemsSource = _components;

            ReportSelectedCount();
        }

        private void ReportSelectedCount()
        {
            SetStatus(_components.Count(c => c.Pick) + " selected");
        }

        private void _dgComponents_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_board == null) return;
            ReportSelectedCount();
        }

        private void Bt_ViewPCB_Click(object sender, RoutedEventArgs e)
        {
            if (_board == null)
            {
                SetStatus("Load a board file first.", InfoBarSeverity.Error);
                return;
            }

            PcbVisualiserWindow pcb = new PcbVisualiserWindow();
            pcb.LoadBoard(_board);
            pcb.Activate();
        }

        private void Bt_eStop_Click(object sender, RoutedEventArgs e)
        {
            _kflop.EStop();
        }

        private void Bt_Stop_Click(object sender, RoutedEventArgs e)
        {
            _app.pcbBuilder.CancelBuildProcess();
        }

        // internal (not private): MainWindow's Stream Deck hotkey handler calls
        // this directly when the Build PCB page is active - see
        // MainWindow.TriggerHomePageAction.
        internal void Bt_Start_Click(object sender, RoutedEventArgs? e)
        {
            if (_board == null)
            {
                SetStatus("Load a board file first.", InfoBarSeverity.Error);
                return;
            }

            if (_app.checkHome())
            {
                bool isHighSpeed = pickdelay.IsChecked == true;
                if (_app.pcbBuilder.ActivateBuildProcess(int.Parse(csfeeder.Text), isHighSpeed))
                {
                    SetStatus("Build started");
                }
            }
            else
            {
                SetStatus("Home the machine before starting a build.", InfoBarSeverity.Error);
            }
            _app.setHomed(false);
        }
    }
}
