using CommunityToolkit.WinUI.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using PickandPlace2026.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace PickandPlace2026.Views
{
    public sealed partial class BoardDesigner : Page
    {
        private class ComponentPickerItem
        {
            public string Code { get; set; } = "";
            public string Value { get; set; } = "";
        }

        private readonly Kflop _kflop;
        private UsbDevice _usbController;
        // Shared App.comp instance, not a page-local Components() - see the comment
        // on PCBBuilder.comp for why a separate instance here would go stale.
        private Components _comp => ((App)Application.Current).comp;
        private readonly Board _board = new Board();
        private readonly ObservableCollection<BoardComponent> _components = new();

        public BoardDesigner()
        {
            InitializeComponent();

            App app = (App)Application.Current;

            // Kflop is constructed synchronously as an App field, so it's
            // available immediately - unlike usbController, which is only
            // created once InitializeHardwareAsync's background task runs
            // (see MainWindow.xaml.cs for the same pattern).
            _kflop = app.GetKFlop();
            _usbController = app.GetUSBDevice();

            app.HardwareStatusChanged += (s, message) =>
            {
                if (message == "Controller Started")
                    _usbController = app.GetUSBDevice();
            };

            KeyDown += OnButtonKeyDown;
            KeyUp += OnButtonKeyUp;

            PopulateComponentList();
            SetupGridView(_dgBoard);

            _dgBoard.ItemsSource = _components;

            WireJogButtons();
        }

        // ButtonBase has class-level handling for PointerPressed that it uses to
        // fire its own Click event - that handling marks the event Handled before
        // any instance handler (including ones declared in XAML) ever sees it.
        // AddHandler's handledEventsToo:true is what actually gets a
        // PointerPressed/Released/Canceled/CaptureLost callback to run on a
        // Button. See ManualControl.xaml.cs for the same pattern/fix.
        private void WireJogButtons()
        {
            WireJogButton(bt_Left, bt_MoveYMinus_PointerPressed);
            WireJogButton(bt_Up, bt_MoveXPlus_PointerPressed);
            WireJogButton(bt_Right, bt_MoveYPlus_PointerPressed);
            WireJogButton(bt_Down, bt_MoveXMinus_PointerPressed);
        }

        private void WireJogButton(Button button, PointerEventHandler pressedHandler)
        {
            button.AddHandler(UIElement.PointerPressedEvent, pressedHandler, true);
            button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(bt_MoveStop), true);
            button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(bt_MoveStop), true);
            button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(bt_MoveStop), true);
        }

        private void SetStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            ((App)Application.Current).ReportStatus(message, severity);
        }

        private double GetJogSpeed()
        {
            ComboBoxItem item = (ComboBoxItem)dd_distance.SelectedItem;
            return double.Parse(item.Content?.ToString() ?? "0");
        }

        private void OnButtonKeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.A:
                    _kflop.JogAxis("X", -GetJogSpeed());
                    break;
                case VirtualKey.D:
                    _kflop.JogAxis("X", GetJogSpeed());
                    break;
                case VirtualKey.W:
                    _kflop.JogAxis("Y", GetJogSpeed());
                    break;
                case VirtualKey.S:
                    _kflop.JogAxis("Y", -GetJogSpeed());
                    break;
            }
        }

        private void OnButtonKeyUp(object sender, KeyRoutedEventArgs e)
        {
            _kflop.JogAxis("X", 0);
            _kflop.JogAxis("Y", 0);
            _kflop.JogAxis("Z", 0);
            _kflop.JogAxis("A", 0);
            _kflop.JogAxis("B", 0);
            _kflop.JogAxis("C", 0);
        }

        private void PopulateComponentList()
        {
            List<Component> components = _comp.LoadComponents();
            var items = components
                .Select(c => new ComponentPickerItem
                {
                    Code = c.ComponentCode.ToString(),
                    Value = c.ComponentValue
                })
                .ToList();

            dd_ComponentSelect.DisplayMemberPath = "Value";
            dd_ComponentSelect.SelectedValuePath = "Code";
            dd_ComponentSelect.ItemsSource = items;
            dd_ComponentSelect.SelectedIndex = 0;
        }

        public void SetupGridView(DataGrid dg)
        {
            dg.AutoGenerateColumns = false;
            AddTextColumn(dg, "Component", "ComponentCode");
            AddTextColumn(dg, "Name", "ComponentName");
            AddTextColumn(dg, "X", "PlacementX");
            AddTextColumn(dg, "Y", "PlacementY");
            AddTextColumn(dg, "Rotate", "PlacementRotate");
            AddTextColumn(dg, "Nozzle", "PlacementNozzle");
        }

        // _dgBoard binds to an ObservableCollection<BoardComponent> - real CLR
        // properties, so a plain property-name path works directly.
        private static void AddTextColumn(DataGrid dg, string header, string binding)
        {
            dg.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding { Path = new PropertyPath(binding) },
                IsReadOnly = false
            });
        }

        private void bt_MoveYMinus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("Y", -GetJogSpeed());
        }

        private void bt_MoveYPlus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("Y", GetJogSpeed());
        }

        private void bt_MoveXMinus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("X", -GetJogSpeed());
        }

        private void bt_MoveXPlus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("X", GetJogSpeed());
        }

        // Shared PointerReleased/PointerCaptureLost/PointerCanceled handler - any of
        // these can fire if the pointer leaves the button while a jog is held down,
        // so all three release the capture and stop every axis to avoid a runaway motor.
        private void bt_MoveStop(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);

            _kflop.JogAxis("X", 0);
            _kflop.JogAxis("Y", 0);
            _kflop.JogAxis("Z", 0);
            _kflop.JogAxis("A", 0);
            _kflop.JogAxis("B", 0);
            _kflop.JogAxis("C", 0);
        }

        private void UpdateDRO()
        {
            double x = 0, y = 0, z = 0, a = 0, b = 0, c = 0;
            _kflop.GetDRO(out x, out y, out z, out a, out b, out c);
            txt_CameraX.Text = x.ToString();
            txt_CameraY.Text = y.ToString();
        }

        private void bt_HomeAll_Click(object sender, RoutedEventArgs e)
        {
            if (_usbController == null)
            {
                SetStatus("USB controller is not connected.", InfoBarSeverity.Error);
                return;
            }

            _usbController.SetResetFeeder();
            _kflop.HomeAll();
        }

        private void bt_GetDRO_Click(object sender, RoutedEventArgs e)
        {
            UpdateDRO();
        }

        private void bt_Camera_Click(object sender, RoutedEventArgs e)
        {
            CameraWindow cam = new CameraWindow();
            cam.Activate();
        }

        private async void bt_SaveFile_Click(object sender, RoutedEventArgs e)
        {
            if (txt_BoardName.Text.Length == 0)
            {
                SetStatus("Please enter the PCB Board name.", InfoBarSeverity.Error);
                return;
            }

            _board.BoardInfo = new BoardInfo
            {
                BoardName = txt_BoardName.Text,
                BoardHeight = double.Parse(txt_BoardHeight.Text),
                MotorRunTime = 20
            };
            _board.Components = _components.ToList();

            App app = (App)Application.Current;
            FileSavePicker picker = new FileSavePicker();
            IntPtr hwnd = WindowNative.GetWindowHandle(app.GetMainWindow());
            InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("JSON file", new List<string> { ".json" });
            picker.SuggestedFileName = txt_BoardName.Text;

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            string json = JsonSerializer.Serialize(_board, BoardJsonContext.Default.Board);
            File.WriteAllText(file.Path, json);

            SetStatus("File saved", InfoBarSeverity.Success);
        }

        private void chk_HeadLED_Toggled(object sender, RoutedEventArgs e)
        {
            _usbController?.SetBaseCameraLED(((ToggleSwitch)sender).IsOn);
        }

        private void bt_addrow_Click(object sender, RoutedEventArgs e)
        {
            // check for valid data and add row to dataset table
            UpdateDRO();
            Thread.Sleep(100);

            if (txt_ComName.Text.Length == 0)
            {
                SetStatus("Please enter the component Ref ID.", InfoBarSeverity.Error);
                return;
            }

            string selectedComponentValue = dd_ComponentSelect.SelectedValue?.ToString() ?? "0";
            int componentCode = int.Parse(selectedComponentValue);
            string componentName = txt_ComName.Text + " - " + _comp.GetComponentValue(selectedComponentValue);
            double placementX = double.Parse(txt_CameraX.Text);
            double placementY = double.Parse(txt_CameraY.Text);
            int placementRotate = int.Parse(txt_Rotate.Text);
            int placementNozzle = check_1.IsChecked == true ? 1 : 2;

            _components.Add(new BoardComponent
            {
                ComponentCode = componentCode,
                ComponentName = componentName,
                PlacementX = placementX,
                PlacementY = placementY,
                PlacementRotate = placementRotate,
                PlacementNozzle = placementNozzle,
                Pick = true
            });

            // Unlike the old DataView-backed grid, ObservableCollection<T>.Add
            // raises CollectionChanged itself, so the DataGrid picks up the new
            // row without needing the ItemsSource-null-reassign workaround.
            SetStatus(_components.Count + " components added");
        }

        private void _dgBoard_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SetStatus(_components.Count + " components");
        }

        private void bt_eStop_Click(object sender, RoutedEventArgs e)
        {
            _kflop.EStop();
        }

        private void Page_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int currentItem = dd_distance.SelectedIndex;
            int maxItems = dd_distance.Items.Count;
            int delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;

            if (delta > 0)
            {
                dd_distance.SelectedIndex = currentItem > 0 ? currentItem - 1 : 0;
            }
            else
            {
                if (currentItem < maxItems - 1)
                {
                    dd_distance.SelectedIndex = currentItem + 1;
                }
            }
        }
    }
}
