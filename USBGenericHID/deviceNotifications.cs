//-----------------------------------------------------------------------------
//
//  deviceNotifications.cs
//
//  USB Generic HID Communications 3_0_0_0
//
//  A class for communicating with Generic HID USB devices
//  Copyright (C) 2011 Simon Inns
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
//  Web:    http://www.waitingforfriday.com
//  Email:  simon.inns@gmail.com
//
//  --- MODIFIED ---
//  This version replaces the original System.Windows.Forms.Control-based
//  window with a native, message-only Win32 window created and pumped on
//  a dedicated background thread. This removes the WinForms dependency
//  entirely so the library can be hosted in non-WinForms UI frameworks
//  (e.g. WinUI 3) without requiring UseWindowsForms in the consuming app.
//
//-----------------------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using System.Threading;

// The following namespace allows debugging output (when compiled in debug mode)
using System.Diagnostics;

namespace usbGenericHidCommunications
{
    /// <summary>
    /// Lightweight stand-in for the (removed) System.Windows.Forms.Message struct.
    /// Carries just the fields this library actually needs from a Win32 window message.
    /// </summary>
    public struct NativeMessage
    {
        public IntPtr HWnd;
        public int Msg;
        public IntPtr WParam;
        public IntPtr LParam;
    }

    /// <summary>
    /// This partial class contains the methods required for detecting when
    /// the USB device is attached or detached.
    /// </summary>
    public partial class usbGenericHidCommunication
    {
        // ---- Native window / message pump plumbing -------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // Keep a reference alive for the lifetime of the window - if this delegate is
        // garbage collected while native code still holds a function pointer to it,
        // you get a crash the first time Windows tries to call it.
        private WndProcDelegate notificationWndProcDelegate;

        private IntPtr notificationWindowHandle = IntPtr.Zero;
        private Thread notificationMessageLoopThread;
        private uint notificationMessageLoopThreadId;
        private readonly ManualResetEventSlim notificationWindowReady = new ManualResetEventSlim(false);
        private string notificationWindowClassName;

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private const uint WM_QUIT = 0x0012;

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>
        /// createNotificationWindow - creates a hidden, message-only native window on a
        /// dedicated background thread with its own message pump, then registers that
        /// window for USB device change notifications. Called once from the class
        /// constructor in place of the old "registerForDeviceNotifications(this.Handle)"
        /// call that relied on a WinForms Control's handle.
        /// </summary>
        private void createNotificationWindow()
        {
            notificationWindowClassName = "usbGenericHidCommunicationsWnd_" + Guid.NewGuid().ToString("N");

            notificationMessageLoopThread = new Thread(notificationMessageLoopThreadProc);
            notificationMessageLoopThread.IsBackground = true;
            notificationMessageLoopThread.SetApartmentState(ApartmentState.STA);
            notificationMessageLoopThread.Start();

            // Wait for the window/message loop to spin up before returning from the
            // constructor, so registerForDeviceNotifications has definitely run.
            if (!notificationWindowReady.Wait(5000))
            {
                Debug.WriteLine("usbGenericHidCommunication:createNotificationWindow() -> Timed out waiting for notification window to initialise");
            }
        }

        private void notificationMessageLoopThreadProc()
        {
            // Store the delegate on the instance so it isn't collected while native code holds a pointer to it
            notificationWndProcDelegate = internalWndProc;

            WNDCLASSEX wndClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                style = 0,
                lpfnWndProc = notificationWndProcDelegate,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = GetModuleHandle(null),
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = notificationWindowClassName,
                hIconSm = IntPtr.Zero
            };

            notificationMessageLoopThreadId = GetCurrentThreadId();

            if (RegisterClassEx(ref wndClass) == 0)
            {
                Debug.WriteLine("usbGenericHidCommunication:notificationMessageLoopThreadProc() -> RegisterClassEx failed");
                notificationWindowReady.Set();
                return;
            }

            // HWND_MESSAGE creates a message-only window: no UI, never visible, but still
            // a fully valid target for RegisterDeviceNotification - Windows delivers
            // WM_DEVICECHANGE directly to the window, it is not relying on being a
            // top-level/visible window to receive it.
            notificationWindowHandle = CreateWindowEx(
                0, notificationWindowClassName, notificationWindowClassName, 0,
                0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

            if (notificationWindowHandle == IntPtr.Zero)
            {
                Debug.WriteLine("usbGenericHidCommunication:notificationMessageLoopThreadProc() -> CreateWindowEx failed");
                notificationWindowReady.Set();
                return;
            }

            // Now that we have a real, valid window handle, register it for device notifications
            registerForDeviceNotifications(notificationWindowHandle);

            notificationWindowReady.Set();

            // Standard Win32 message pump - required for this thread's window to receive
            // any messages at all, including WM_DEVICECHANGE.
            MSG msg;
            int getMessageResult;
            while ((getMessageResult = GetMessage(out msg, IntPtr.Zero, 0, 0)) != 0)
            {
                if (getMessageResult == -1) break; // GetMessage error
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (notificationWindowHandle != IntPtr.Zero)
            {
                DestroyWindow(notificationWindowHandle);
                notificationWindowHandle = IntPtr.Zero;
            }

            UnregisterClass(notificationWindowClassName, wndClass.hInstance);
        }

        /// <summary>
        /// destroyNotificationWindow - signals the message pump thread to exit and clean
        /// up the notification window. Call this from the class destructor in place of
        /// the previous automatic WinForms Control teardown.
        /// </summary>
        private void destroyNotificationWindow()
        {
            if (notificationWindowHandle != IntPtr.Zero)
            {
                UnregisterDeviceNotification(deviceInformation.deviceNotificationHandle);
            }

            if (notificationMessageLoopThread != null && notificationMessageLoopThread.IsAlive)
            {
                // Post WM_QUIT directly to the pump thread's queue so GetMessage() returns 0 and the loop exits
                PostThreadMessage(notificationMessageLoopThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                notificationMessageLoopThread.Join(2000);
            }
        }

        private IntPtr internalWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            NativeMessage m = new NativeMessage
            {
                HWnd = hWnd,
                Msg = (int)msg,
                WParam = wParam,
                LParam = lParam
            };

            handleDeviceNotificationMessages(m);

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // ---- USB device notification logic (unchanged from the original) ---------

        /// <summary>
        /// Create a delegate for the USB event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public delegate void usbEventsHandler(object sender, EventArgs e);

        /// <summary>
        /// Define the event
        /// </summary>
        public event usbEventsHandler usbEvent;

        /// <summary>
        /// The usb event thrower
        /// </summary>
        /// <param name="e"></param>
        protected virtual void onUsbEvent(EventArgs e)
        {
            if (usbEvent != null)
            {
                Debug.WriteLine("usbGenericHidCommunications:onUsbEvent() -> Throwing a USB event to a listener");
                usbEvent(this, e);
            }
            else Debug.WriteLine("usbGenericHidCommunications:onUsbEvent() -> Attempted to throw a USB event, but no one was listening");
        }

        /// <summary>
        /// isNotificationForTargetDevice - Compares the target devices pathname against the
        /// pathname of the device which caused the event message
        /// </summary>
        private Boolean isNotificationForTargetDevice(NativeMessage m)
        {
            Int32 stringSize;

            try
            {
                DEV_BROADCAST_DEVICEINTERFACE_1 devBroadcastDeviceInterface = new DEV_BROADCAST_DEVICEINTERFACE_1();
                DEV_BROADCAST_HDR devBroadcastHeader = new DEV_BROADCAST_HDR();

                Marshal.PtrToStructure(m.LParam, devBroadcastHeader);

                // Is the notification event concerning a device interface?
                if ((devBroadcastHeader.dbch_devicetype == DBT_DEVTYP_DEVICEINTERFACE))
                {
                    // Get the device path name of the affected device
                    stringSize = System.Convert.ToInt32((devBroadcastHeader.dbch_size - 32) / 2);
                    devBroadcastDeviceInterface.dbcc_name = new Char[stringSize + 1];
                    Marshal.PtrToStructure(m.LParam, devBroadcastDeviceInterface);
                    String deviceNameString = new String(devBroadcastDeviceInterface.dbcc_name, 0, stringSize);

                    // Compare the device name with our target device's pathname (strings are moved to lower case
                    // using en-US to ensure case insensitivity accross different regions)
                    if ((String.Compare(deviceNameString.ToLower(new System.Globalization.CultureInfo("en-US")),
                        deviceInformation.devicePathName.ToLower(new System.Globalization.CultureInfo("en-US")), true) == 0)) return true;
                    else return false;
                }
            }
            catch (Exception)
            {
                Debug.WriteLine("usbGenericHidCommunication:isNotificationForTargetDevice() -> EXCEPTION: An unknown exception has occured!");
                return false;
            }
            return false;
        }

        /// <summary>
        /// registerForDeviceNotification - registers the window (identified by the windowHandle) for 
        /// device notification messages from Windows
        /// </summary>
        public Boolean registerForDeviceNotifications(IntPtr windowHandle)
        {
            Debug.WriteLine("usbGenericHidCommunication:registerForDeviceNotifications() -> Method called");

            // A DEV_BROADCAST_DEVICEINTERFACE header holds information about the request.
            DEV_BROADCAST_DEVICEINTERFACE devBroadcastDeviceInterface = new DEV_BROADCAST_DEVICEINTERFACE();
            IntPtr devBroadcastDeviceInterfaceBuffer = IntPtr.Zero;
            Int32 size = 0;

            // Get the required GUID
            System.Guid systemHidGuid = new Guid();
            HidD_GetHidGuid(ref systemHidGuid);

            try
            {
                // Set the parameters in the DEV_BROADCAST_DEVICEINTERFACE structure.
                size = Marshal.SizeOf(devBroadcastDeviceInterface);
                devBroadcastDeviceInterface.dbcc_size = size;
                devBroadcastDeviceInterface.dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE;
                devBroadcastDeviceInterface.dbcc_reserved = 0;
                devBroadcastDeviceInterface.dbcc_classguid = systemHidGuid;

                devBroadcastDeviceInterfaceBuffer = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(devBroadcastDeviceInterface, devBroadcastDeviceInterfaceBuffer, true);

                // Register for notifications and store the returned handle
                deviceInformation.deviceNotificationHandle = RegisterDeviceNotification(windowHandle, devBroadcastDeviceInterfaceBuffer, DEVICE_NOTIFY_WINDOW_HANDLE);
                Marshal.PtrToStructure(devBroadcastDeviceInterfaceBuffer, devBroadcastDeviceInterface);

                if ((deviceInformation.deviceNotificationHandle.ToInt32() == IntPtr.Zero.ToInt32()))
                {
                    Debug.WriteLine("usbGenericHidCommunication:registerForDeviceNotifications() -> Notification registration failed");
                    return false;
                }
                else
                {
                    Debug.WriteLine("usbGenericHidCommunication:registerForDeviceNotifications() -> Notification registration succeded");
                    return true;
                }
            }
            catch (Exception)
            {
                Debug.WriteLine("usbGenericHidCommunication:registerForDeviceNotifications() -> EXCEPTION: An unknown exception has occured!");
            }
            finally
            {
                // Free the memory allocated previously by AllocHGlobal.
                if (devBroadcastDeviceInterfaceBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(devBroadcastDeviceInterfaceBuffer);
            }
            return false;
        }

        /// <summary>
        /// handleDeviceNotificationMessages - this method examines any windows devices messages that are
        /// received to check if they are relevant to our target USB device.  If so the method takes the 
        /// correct action dependent on the message type.
        /// </summary>
        /// <param name="m"></param>
        public void handleDeviceNotificationMessages(NativeMessage m)
        {
            //Debug.WriteLine("usbGenericHidCommunication:handleDeviceNotificationMessages() -> Method called");

            // Make sure this is a device notification
            if (m.Msg != WM_DEVICECHANGE) return;

            Debug.WriteLine("usbGenericHidCommunication:handleDeviceNotificationMessages() -> Device notification received");

            try
            {
                switch (m.WParam.ToInt32())
                {
                    // Device attached
                    case DBT_DEVICEARRIVAL:
                        Debug.WriteLine("usbGenericHidCommunication:handleDeviceNotificationMessages() -> New device attached");
                        // If our target device is not currently attached, this could be our device, so we attempt to find it.
                        if (!isDeviceAttached)
                        {
                            findTargetDevice();
                            onUsbEvent(EventArgs.Empty); // Generate an event
                        }
                        break;

                    // Device removed
                    case DBT_DEVICEREMOVECOMPLETE:
                        Debug.WriteLine("usbGenericHidCommunication:handleDeviceNotificationMessages() -> A device has been removed");

                        // Was this our target device?  
                        if (isNotificationForTargetDevice(m))
                        {
                            // If so detach the USB device.
                            Debug.WriteLine("usbGenericHidCommunication:handleDeviceNotificationMessages() -> The target USB device has been removed - detaching...");
                            detachUsbDevice();
                            onUsbEvent(EventArgs.Empty); // Generate an event
                        }
                        break;

                    // Other message
                    default:
                        Debug.WriteLine("usbGenericHidCommunication:handleDeviceNotificationMessages() -> Unknown notification message");
                        break;
                }
            }
            catch (Exception)
            {
                Debug.WriteLine("usbGenericHidCommunication:handleDeviceNotificationMessages() -> EXCEPTION: An unknown exception has occured!");
            }
        }
    }
}