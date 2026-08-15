using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using PickandPlace2026.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PickandPlace2026
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        // KMotion_dotNet's KM_Controller P/Invokes into KMotionDLL.dll, which
        // lives in the KMotion install, not next to this app. .NET Core/5+ (unlike
        // .NET Framework 4) does NOT search the process's current directory for
        // native DLLs by default, so we add this directory to the native search
        // path explicitly instead. This must run before any Kflop/KM_Controller
        // code executes - do not replace this with a Deploy/LayoutDir-based
        // approach, since MSIX Deploy's RemoveNonLayoutFiles cleanup will delete
        // any pre-existing files in whatever directory LayoutDir points at.
        private const string KMotionNativeDir = @"C:\KMotion5.4.5\KMotion\Release64";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        private Window? _window;

        // Not set by the constructor - assigned once InitializeHardwareAsync()
        // runs (fired from OnLaunched, after the window is up). GetUSBDevice()
        // callers check for null themselves before this first completes.
        public UsbDevice usbController = null!;
        public Kflop _kflop = new Kflop();
        // public FilterInfoCollection videoDevices;
        //public VideoCaptureDevice videoSource;

        public bool _hasHomed = false;

        public DataComponentFeeders cf = new DataComponentFeeders();
        public Components comp = new Components();
        public PCBBuilder pcbBuilder = new PCBBuilder();

        public AppSettings Settings { get; private set; }

        /// <summary>
        /// Raised when hardware initialization succeeds or fails, and whenever the
        /// USB device attaches/detaches. May fire on a background thread (hardware
        /// init runs via Task.Run, and usbEvent can fire from the notification
        /// window's message pump thread) - subscribers must marshal to the UI
        /// thread themselves, e.g. via DispatcherQueue.TryEnqueue, same as
        /// PCBBuilder.ErrorOccurred.
        /// </summary>
        public event EventHandler<string>? HardwareStatusChanged;

        /// <summary>
        /// Raised whenever any page wants to show a status message. MainWindow is
        /// the sole subscriber and displays it in its own InfoBar, so pages don't
        /// each need their own InfoBar/SetStatus - see ReportStatus below. May fire
        /// from a background thread, same as HardwareStatusChanged.
        /// </summary>
        public event EventHandler<(string Message, InfoBarSeverity Severity)>? StatusRequested;

        public void ReportStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational) =>
            StatusRequested?.Invoke(this, (message, severity));

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            SetDllDirectory(KMotionNativeDir);

            InitializeComponent();

            _hasHomed = false;
            Settings = AppSettings.Load();

            _kflop.ErrorOccurred += (s, message) =>
                HardwareStatusChanged?.Invoke(this, "Kflop error: " + message);
            pcbBuilder.ErrorOccurred += (s, message) =>
                HardwareStatusChanged?.Invoke(this, "PCB Builder error: " + message);

            // Hardware initialization deliberately does NOT happen here. At this
            // point in the app lifecycle there is no window yet, so there is
            // nowhere to report a failure to, and anything slow here delays the
            // window from appearing at all. See InitializeHardwareAsync(), which
            // runs after the window is created and up.
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();

            // Fire-and-forget: the window is already up, so a slow or failed
            // hardware connection no longer blocks startup. Status/errors are
            // reported via HardwareStatusChanged for MainWindow to display.
            _ = InitializeHardwareAsync();
        }

        private async Task InitializeHardwareAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    usbController = new UsbDevice(0x04D8, 0x0042);
                    usbController.usbEvent += new UsbDevice.usbEventsHandler(usbEvent_receiver);
                   
                    usbController.findTargetDevice();
                    usbController.RunBoardInit(true, 250, 250);
                });
                HardwareStatusChanged?.Invoke(this, usbController.isDeviceAttached
                    ? "Controller Started"
                    : "Controller not found - USB device did not attach");

            }
            catch (Exception ex)
            {
                HardwareStatusChanged?.Invoke(this, "Error connecting to controller: " + ex.Message);
            }

            // Kflop init runs as an independent step: a missing/unresponsive
            // KMotion board should not prevent the USB controller above from
            // working, and vice versa (e.g. USB-only bench testing).
            try
            {
                await Task.Run(() => _kflop.InitDevice());

                // InitDeviceSettings() deliberately doesn't throw when the KFLOP
                // init program fails to load - it RaiseError()s and returns early
                // instead, to avoid making hardware calls against a controller
                // that isn't in a valid state. That means this catch block alone
                // can't tell init failed - IsReady is the only reliable signal.
                // Without this check, "Kflop Started" fired unconditionally right
                // after, overwriting the real error in the same status bar with a
                // false success message.
                HardwareStatusChanged?.Invoke(this, _kflop.IsReady
                    ? "Kflop Started"
                    : "Error: Kflop failed to initialize - axes not enabled. Homing and builds will not move the machine.");
            }
            catch (Exception ex)
            {
                HardwareStatusChanged?.Invoke(this, "Error connecting to Kflop: " + ex.Message);
            }
        }

        public bool checkHome()
        {
            return _hasHomed;
        }

        public void setHomed(bool status)
        {
            _hasHomed = status;
        }

        public Kflop GetKFlop()
        {
            return _kflop;
        }

        public UsbDevice GetUSBDevice()
        {
            return usbController;
        }

        public Window GetMainWindow()
        {
            // _window is assigned in OnLaunched(), before any page (the only
            // callers of this method) can exist to call it.
            return _window!;
        }

        private void usbEvent_receiver(object o, EventArgs e)
        {
            // Check the status of the USB device and update the form accordingly
            if (usbController.isDeviceAttached)
            {
                HardwareStatusChanged?.Invoke(this, "USB device attached");
            }
            else
            {
                HardwareStatusChanged?.Invoke(this, "USB device detached");
            }
        }
    }
}