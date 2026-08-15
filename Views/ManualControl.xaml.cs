using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PickandPlace2026.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace PickandPlace2026.Views
{
    public sealed partial class ManualControl : Page
    {
        private class ComponentPickerItem
        {
            public string Code { get; set; } = "";
            public string Value { get; set; } = "";
        }

        private readonly App _app;
        private readonly Kflop _kflop;
        private readonly Components _comp = new Components();
        private UsbDevice _usbController;

        public KflopLocation kfl = new KflopLocation(0, 100, 100, 0, 0, 0, 0, 0, 0, 0, false, false);
        public double Nozzle1Xoffset = 0;
        public double Nozzle1Yoffset = 0;
        public double Nozzle2Xoffset = 0;
        public double Nozzle2Yoffset = 0;
        public double ClearHeight;

        private volatile bool _pollRunning = true;
        private Thread _pollThread;

        public ManualControl()
        {
            InitializeComponent();

            _app = (App)Application.Current;

            ClearHeight = _app.Settings.ClearHeight;

            // Kflop is constructed synchronously as an App field, so it's
            // available immediately - unlike usbController, which is only
            // created once InitializeHardwareAsync's background task runs
            // (see MainWindow.xaml.cs for the same pattern).
            _kflop = _app.GetKFlop();
            _usbController = _app.GetUSBDevice();

            _kflop.ErrorOccurred += (s, message) => SetStatus(message, InfoBarSeverity.Error);

            _app.HardwareStatusChanged += (s, message) =>
            {
                if (message == "Controller Started")
                    _usbController = _app.GetUSBDevice();
            };

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

            // Live E-Stop/Homed indicators (the dots next to the Home/E-Stop buttons in
            // the XAML). Polls on a dedicated background thread rather than the UI
            // thread so a slow hardware call or an in-progress Home run can't block
            // jog button PointerPressed handlers or anything else on the UI thread.
            _pollThread = new Thread(PollLoop) { IsBackground = true };
            _pollThread.Start();

            Unloaded += (s, e) => _pollRunning = false;

            WireJogButtons();
        }

        // ButtonBase has class-level handling for PointerPressed that it uses to
        // fire its own Click event - that handling marks the event Handled before
        // any instance handler (including ones declared in XAML, like these used
        // to be) ever sees it. AddHandler's handledEventsToo:true is what actually
        // gets a PointerPressed/Released/Canceled/CaptureLost callback to run on a
        // Button. This is the real reason none of the jog buttons ever worked -
        // every other fix this session was solving separate, real bugs, but none
        // of them could have touched this.
        private void WireJogButtons()
        {
            WireJogButton(bt_Left, bt_MoveYMinus_PointerPressed);
            WireJogButton(bt_Up, bt_MoveXPlus_PointerPressed);
            WireJogButton(bt_Right, bt_MoveYPlus_PointerPressed);
            WireJogButton(bt_Down, bt_MoveXMinus_PointerPressed);
            WireJogButton(bt_ZUp, bt_MoveZMinus_PointerPressed);
            WireJogButton(bt_ZDown, bt_MoveZPlus_PointerPressed);
            WireJogButton(bt_AUp, bt_MoveAMinus_PointerPressed);
            WireJogButton(bt_ADown, bt_MoveAPlus_PointerPressed);
            WireJogButton(bt_BLeft, bt_MoveBMinus_PointerPressed);
            WireJogButton(bt_BRight, bt_MoveBPlus_PointerPressed);
            WireJogButton(bt_CLeft, bt_MoveCMinus_PointerPressed);
            WireJogButton(bt_CRight, bt_MoveCPlus_PointerPressed);
        }

        private void WireJogButton(Button button, PointerEventHandler pressedHandler)
        {
            button.AddHandler(UIElement.PointerPressedEvent, pressedHandler, true);
            button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(bt_MoveStop), true);
            button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(bt_MoveStop), true);
            button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(bt_MoveStop), true);
        }

        private void PollLoop()
        {
            while (_pollRunning)
            {
                try
                {
                    // eStopActive/checkHome() are cheap field reads, not hardware I/O,
                    // but still read off the UI thread and marshaled back so this loop
                    // stays consistent if either ever becomes more expensive.
                    bool eStopActive = _kflop.eStopActive;
                    bool homed = ((App)Application.Current).checkHome();

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ind_EStop.Background = new SolidColorBrush(eStopActive ? Colors.Red : Colors.LimeGreen);
                        txt_EStop.Text = eStopActive ? "E-Stop Active" : "Ready";

                        ind_Homed.Background = new SolidColorBrush(homed ? Colors.LimeGreen : Colors.Gray);
                        txt_Homed.Text = homed ? "Homed" : "Not Homed";
                    });
                }
                catch (Exception ex)
                {
                    // A throw from eStopActive/checkHome would silently kill this whole
                    // background thread, freezing both indicators with no visible error
                    // anywhere. Log and keep polling.
                    Debug.WriteLine("PollLoop iteration failed: " + ex);
                }

                Thread.Sleep(250);
            }
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

        private void bt_MoveYMinus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            SetStatus("bt_MoveYMinus_PointerPressed");
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

        private void bt_MoveZMinus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("Z", -GetJogSpeed());
        }

        private void bt_MoveZPlus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("Z", GetJogSpeed());
        }

        private void bt_MoveAMinus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("A", -GetJogSpeed());
        }

        private void bt_MoveAPlus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("A", GetJogSpeed());
        }

        private void bt_MoveBMinus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("B", -GetJogSpeed());
        }

        private void bt_MoveBPlus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("B", GetJogSpeed());
        }

        private void bt_MoveCMinus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("C", -GetJogSpeed());
        }

        private void bt_MoveCPlus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            _kflop.JogAxis("C", GetJogSpeed());
        }

        // Shared PointerReleased/PointerCaptureLost/PointerCanceled handler for every
        // jog button - any of those can fire if the pointer leaves the button (or the
        // app loses focus) while a jog is held down, so all three release the pointer
        // capture and stop every axis to avoid a runaway motor.
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
            txt_CameraZ.Text = z.ToString();
            txt_CameraA.Text = a.ToString();
            txt_CameraB.Text = b.ToString();
            txt_CameraC.Text = c.ToString();
        }

        private void bt_Camera_Click(object sender, RoutedEventArgs e)
        {
            CameraWindow cam = new CameraWindow();
            cam.Activate();
        }

        private void bt_HomeAll_Click(object sender, RoutedEventArgs e)
        {
            _kflop.HomeAll();
            _usbController?.SetResetFeeder();
        }

        private void bt_GetDRO_Click(object sender, RoutedEventArgs e)
        {
            UpdateDRO();
        }


        private void chk_HeadLED_Toggled(object sender, RoutedEventArgs e)
        {
            _usbController?.SetBaseCameraLED(((ToggleSwitch)sender).IsOn);
        }

        private void chk_BaseLED_Toggled(object sender, RoutedEventArgs e)
        {
            _usbController?.SetHeadCameraLED(((ToggleSwitch)sender).IsOn);
        }

        private void chk_Vac1_Toggled(object sender, RoutedEventArgs e)
        {
            _usbController?.SetVAC1(((ToggleSwitch)sender).IsOn);
        }

        private void chk_Vac2_Toggled(object sender, RoutedEventArgs e)
        {
            _usbController?.SetVAC2(((ToggleSwitch)sender).IsOn);
        }

        private void bt_getFeeder_Click(object sender, RoutedEventArgs e)
        {
            if (_usbController == null)
            {
                SetStatus("USB controller is not connected.", InfoBarSeverity.Error);
                return;
            }

            ComboBoxItem item = (ComboBoxItem)dd_FeederSelect.SelectedItem;
            byte feeder = byte.Parse(item.Content?.ToString() ?? "0");
            _usbController.SetGotoFeeder(feeder);
        }

        private void bt_eStop_Click(object sender, RoutedEventArgs e)
        {
            _kflop.EStop();
        }

        private void bt_ChipFeeder_Click(object sender, RoutedEventArgs e)
        {
            _usbController?.RunVibrationMotor(25);
        }

        private void bt_runto_Click(object sender, RoutedEventArgs e)
        {
            double newX = double.Parse(txt_goX.Text);
            double newY = double.Parse(txt_goY.Text);
            double newZ = double.Parse(txt_goZ.Text);
            double newA = double.Parse(txt_goA.Text);
            double newB = double.Parse(txt_goB.Text);
            double newC = double.Parse(txt_goC.Text);
            double newSpeed = num_Speed.Value;

            ThreadStart starter = () => _kflop.MoveSingleFeed(newSpeed, newX, newY, newZ, newA, newB, newC);
            new Thread(starter).Start();
        }

        // run to selected component
        private void bt_selcomp_Click(object sender, RoutedEventArgs e)
        {
            if (_usbController == null)
            {
                SetStatus("USB controller is not connected.", InfoBarSeverity.Error);
                return;
            }

            Nozzle1Xoffset = _app.Settings.Nozzle1Xoffset;
            Nozzle1Yoffset = _app.Settings.Nozzle1Yoffset;
            Nozzle2Xoffset = _app.Settings.Nozzle2Xoffset;
            Nozzle2Yoffset = _app.Settings.Nozzle2Yoffset;

            int componentCode = int.Parse(dd_ComponentSelect.SelectedValue?.ToString() ?? "0");
            string componentName = _comp.GetComponentValue(componentCode.ToString());

            kfl.PlacementNozzle = componentCode;
            kfl.PickSpeed = _app.Settings.PickSpeed;
            kfl.PlaceSpeed = _comp.GetPlaceSpeed(componentCode.ToString(), 50);

            kfl.FeederX = CalcXLocation(_comp.GetFeederX(componentCode.ToString()), kfl.PlacementNozzle);
            kfl.FeederY = CalcYLocation(_comp.GetFeederY(componentCode.ToString()), kfl.PlacementNozzle);
            kfl.FeederHeight = _comp.GetFeederHeight(componentCode.ToString());

            kfl.PlaceHeight = _comp.GetPlacementHeight(componentCode.ToString());

            kfl.TapeFeeder = _comp.GetComponentTapeFeeder(componentCode.ToString());

            kfl.PlaceX = 0;
            kfl.PlaceY = 0;
            kfl.PlaceRotation = 0;

            kfl.VerifyCamera = false;

            SetFeederOutputs(_comp.GetFeederID(componentCode.ToString())); // send feeder to position

            _kflop.MoveSingleFeed(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, ClearHeight, ClearHeight, 0, 0);

            if (_comp.GetComponentTapeFeeder(componentCode.ToString()))
            {
                while (!_usbController.GetFeederReadyStatus())
                {
                    Thread.Sleep(10);
                }
                Thread.Sleep(50);

                if (kfl.PlacementNozzle == 1)
                {
                    // use picker 1
                    _kflop.MoveSingleFeed(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, kfl.FeederHeight, ClearHeight, 0, 0);
                }
                else
                {
                    // nozzle 2 on tape feeder
                    _kflop.MoveSingleFeed(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, ClearHeight, kfl.FeederHeight, 0, 0);
                    Thread.Sleep(200);
                }
            }
            else
            {
                // use picker 2
                while (_usbController.CheckChipMotorRunning())
                {
                    Thread.Sleep(10);
                }
                _kflop.MoveSingleFeed(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, ClearHeight, kfl.FeederHeight, 0, 0);
            }
        }

        public double CalcXLocation(double val, int nozzle)
        {
            return nozzle == 1 ? val - Nozzle1Xoffset : val - Nozzle2Xoffset;
        }

        public double CalcYLocation(double val, int nozzle)
        {
            return nozzle == 1 ? val - Nozzle1Yoffset : val - Nozzle2Yoffset;
        }

        public void SetFeederOutputs(int feedercommand)
        {
            _usbController.SetGotoFeeder(byte.Parse(feedercommand.ToString()));

            // check if on main feeder rack
            if (feedercommand == 98)
            {
                // command set, now toggle interrupt pin
                _usbController.SetResetFeeder();
            }
            if (feedercommand >= 20 && feedercommand < 30)
            {
                _usbController.RunVibrationMotor(20);
            }
        }
    }
}
