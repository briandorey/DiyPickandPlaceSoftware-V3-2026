using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PickandPlace2026.Classes;
using PickandPlace2026.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics;
using Windows.UI.ApplicationSettings;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PickandPlace2026
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {

        UsbDevice? usbController;
        Kflop _kflop;
        bool IsVacClear = false;

        // Tag of whatever page NavView_SelectionChanged last genuinely
        // navigated to. Button_Home/Button_FlushVac live in NavView.PaneFooter
        // rather than as NavigationViewItems - clicking them has been observed
        // resetting NavView.SelectedItem back to the first menu item ("home"),
        // which fires NavView_SelectionChanged for real and navigates
        // ContentFrame away from whatever page was actually showing. Snapshot
        // here (via PointerPressed, the earliest hook on the button, before
        // Click and before that reset appears to happen) and restore
        // afterward if it turns out to have changed.
        private string _currentNavTag = "home";
        private string? _paneFooterClickSnapshotTag;

        // Elgato Stream Deck buttons are configured (in the Stream Deck app) to
        // send these via its built-in "Hotkey" system action - no plugin needed.
        // F13-F17 have no physical key on a standard keyboard, so registering
        // them as global hotkeys here can't collide with any other app's
        // shortcuts. RegisterHotKey delivers WM_HOTKEY to this window regardless
        // of which app has focus, which matters most for E-Stop.
        private const int HOTKEY_START = 1;
        private const int HOTKEY_HOME = 2;
        private const int HOTKEY_ESTOP = 3;
        private const int HOTKEY_CHIPFEEDER = 4;
        private const int HOTKEY_UNCHECKALL = 5;
        private const int HOTKEY_CHECKALL = 6;

        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_F13 = 0x7C;
        private const uint VK_F14 = 0x7D;
        private const uint VK_F15 = 0x7E;
        private const uint VK_F16 = 0x7F;
        private const uint VK_F17 = 0x80;
        private const uint VK_F18 = 0x81;

        private const int WM_HOTKEY = 0x0312;
        private const int GWLP_WNDPROC = -4;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // Only SetWindowLongPtr exists on 64-bit user32; 32-bit builds (win-x86
        // is one of this project's RuntimeIdentifiers) only have SetWindowLong.
        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private IntPtr _hwnd;
        private IntPtr _prevWndProc;
        // Kept as a field, not a local - if this delegate gets garbage collected,
        // the native callback SetWindowLongPtr installed below becomes a
        // dangling pointer and the app crashes the next time Windows calls it.
        // Assigned synchronously in RegisterStreamDeckHotkeys(), called
        // unconditionally from this constructor before it returns.
        private WndProcDelegate _wndProc = null!;

        public MainWindow()
        {
            this.InitializeComponent();

            ConfigureAppWindow();
            RegisterStreamDeckHotkeys();

            Button_Home.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler((s, e) => _paneFooterClickSnapshotTag = _currentNavTag), true);
            Button_FlushVac.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler((s, e) => _paneFooterClickSnapshotTag = _currentNavTag), true);

            App app = (App)Application.Current;

            // Kflop is constructed synchronously as an App field, so it's
            // available immediately - unlike usbController, which is only
            // created once InitializeHardwareAsync's background task runs.
            _kflop = app.GetKFlop();

            app.HardwareStatusChanged += (sender, message) =>
            {
                if (message == "Controller Started")
                    usbController = app.GetUSBDevice();
                SetStatus(message, message.StartsWith("Error") ? InfoBarSeverity.Error : InfoBarSeverity.Informational);
            };

            // Every page reports its status through App.ReportStatus instead of
            // owning its own InfoBar, so this window's InfoBar is the single place
            // status messages are shown regardless of which page is active.
            app.StatusRequested += (sender, status) => SetStatus(status.Message, status.Severity);

            // ContentFrame otherwise stays blank until the user clicks a nav item -
            // nothing ever navigates it on startup. Setting SelectedItem here (once
            // NavView's item containers actually exist) routes through the normal
            // NavView_SelectionChanged handler below, so this is the only place
            // that triggers a Frame.Navigate call.
            NavView.Loaded += (s, e) => NavView.SelectedItem = NavItem_Home;
        }

        // With nothing sizing it explicitly, this window was falling back to
        // whatever oversized default WinUI picks - CameraWindow already does
        // this same explicit Resize/centered-Move pattern for the same reason.
        // Stays resizable/maximizable (unlike CameraWindow), this is just the
        // initial size.
        private void ConfigureAppWindow()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new SizeInt32(1300, 850));

            // <ApplicationIcon> in the csproj only embeds the icon into the
            // compiled exe (what Explorer/taskbar show before the app is
            // running) - the running window's own title-bar icon has to be set
            // explicitly at runtime, hence this. Same AppContext.BaseDirectory-
            // relative pattern Components.cs/Kflop.cs already use for other
            // runtime assets, since this app runs unpackaged.
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

            DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                int x = (displayArea.WorkArea.Width - appWindow.Size.Width) / 2;
                int y = (displayArea.WorkArea.Height - appWindow.Size.Height) / 2;
                appWindow.Move(new PointInt32(x, y));
            }
        }

        private void SetStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                StatusInfoBar.Message = message;
                StatusInfoBar.Severity = severity;
                StatusInfoBar.IsOpen = true;
            });
        }

        private void RegisterStreamDeckHotkeys()
        {
            _hwnd = WindowNative.GetWindowHandle(this);

            // Installing this subclass is what lets HotkeyWndProc see WM_HOTKEY -
            // WinUI3 doesn't expose a message-loop hook the way WPF's HwndSource
            // AddHook does.
            _wndProc = HotkeyWndProc;
            _prevWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));

            RegisterHotKey(_hwnd, HOTKEY_START, MOD_NOREPEAT, VK_F13);
            RegisterHotKey(_hwnd, HOTKEY_HOME, MOD_NOREPEAT, VK_F14);
            RegisterHotKey(_hwnd, HOTKEY_ESTOP, MOD_NOREPEAT, VK_F15);
            RegisterHotKey(_hwnd, HOTKEY_CHIPFEEDER, MOD_NOREPEAT, VK_F16);
            RegisterHotKey(_hwnd, HOTKEY_UNCHECKALL, MOD_NOREPEAT, VK_F17);
            RegisterHotKey(_hwnd, HOTKEY_CHECKALL, MOD_NOREPEAT, VK_F18);

            this.Closed += (s, e) =>
            {
                UnregisterHotKey(_hwnd, HOTKEY_START);
                UnregisterHotKey(_hwnd, HOTKEY_HOME);
                UnregisterHotKey(_hwnd, HOTKEY_ESTOP);
                UnregisterHotKey(_hwnd, HOTKEY_CHIPFEEDER);
                UnregisterHotKey(_hwnd, HOTKEY_UNCHECKALL);
                UnregisterHotKey(_hwnd, HOTKEY_CHECKALL);
                SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _prevWndProc);
            };
        }

        private IntPtr HotkeyWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY)
            {
                switch (wParam.ToInt32())
                {
                    case HOTKEY_START:
                        TriggerHomePageAction(p => p.Bt_Start_Click(p, null), "Start");
                        break;
                    case HOTKEY_HOME:
                        Button_Home_Click(this, null);
                        break;
                    case HOTKEY_ESTOP:
                        _kflop.EStop();
                        SetStatus("E-STOP triggered", InfoBarSeverity.Error);
                        break;
                    case HOTKEY_CHIPFEEDER:
                        TriggerHomePageAction(p => p.Bt_ChipFeeder_Click(p, null), "Chip Feeder");
                        break;
                    case HOTKEY_UNCHECKALL:
                        TriggerHomePageAction(p => p.Bt_UnCheckAll_Click(p, null), "Uncheck All");
                        break;
                    case HOTKEY_CHECKALL:
                        TriggerHomePageAction(p => p.Bt_CheckAll_Click(p, null), "Check All");
                        break;
                }
                return IntPtr.Zero;
            }

            return CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
        }

        // Start/Chip Feeder/Uncheck All operate on state (loaded board, feeder
        // number) that only exists on HomePage, so they only make sense while
        // "Build PCB" is the active page - unlike Home/E-Stop, which this window
        // can handle itself regardless of which page is showing.
        private void TriggerHomePageAction(Action<HomePage> action, string actionName)
        {
            if (ContentFrame.Content is HomePage homePage)
                action(homePage);
            else
                SetStatus($"Open the Build PCB page to use the {actionName} Stream Deck button", InfoBarSeverity.Warning);
        }

        private void Button_Home_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs? e)
        {
            SetStatus("Home Clicked");
            if (usbController != null)
            {
                bool resetOk = usbController.SetResetFeeder();
                _kflop.HomeAll();

                SetStatus(
                    resetOk ? "Homing complete" : "Home failed: feeder reset did not respond",
                    resetOk ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }

            RestorePaneSelectionIfNeeded();
        }

        private void Button_FlushVac_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (usbController != null)
            {
                bool vac1Ok, vac2Ok;

                if (!IsVacClear)
                {
                    vac1Ok = usbController.SetVAC1(true);
                    vac2Ok = usbController.SetVAC2(true);
                    IsVacClear = true;
                }
                else
                {
                    vac1Ok = usbController.SetVAC1(false);
                    vac2Ok = usbController.SetVAC2(false);
                    IsVacClear = false;
                }

                bool success = vac1Ok && vac2Ok;
                SetStatus(
                    success ? "Vacuum flushed" : "Vacuum command failed - check device connection",
                    success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            } else
            {
                SetStatus("USB Controller is null");
            }

            RestorePaneSelectionIfNeeded();
        }

        // Deferred via TryEnqueue so this runs after any pending selection-
        // reset from clicking the pane-footer button has already happened -
        // otherwise checking immediately could run before that reset lands.
        private void RestorePaneSelectionIfNeeded()
        {
            string? expectedTag = _paneFooterClickSnapshotTag;
            if (expectedTag == null) return;

            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (_currentNavTag == expectedTag) return;

                foreach (NavigationViewItem item in NavView.MenuItems.OfType<NavigationViewItem>())
                {
                    if (item.Tag as string == expectedTag)
                    {
                        NavView.SelectedItem = item;
                        break;
                    }
                }
            });
        }

        private void NavView_SelectionChanged(NavigationView sender,
    NavigationViewSelectionChangedEventArgs args)
        {
            // Otherwise a status message from the page being left stays showing
            // over the page being navigated to, which reads as if it came from
            // the new page.
            StatusInfoBar.IsOpen = false;

            if (args.IsSettingsSelected)
            {
                _currentNavTag = "settings";
                ContentFrame.Navigate(typeof(SettingsPage));
                return;
            }

            if (args.SelectedItemContainer?.Tag is string tag)
            {
                _currentNavTag = tag;
                Type pageType = tag switch
                {
                    "home" => typeof(HomePage),
                    "components" => typeof(ComponentsPage),
                    "designer" => typeof(BoardDesigner),
                    "manual" => typeof(ManualControl),
                    _ => typeof(HomePage)
                };
                ContentFrame.Navigate(pageType);
            }
        }
    }
}
