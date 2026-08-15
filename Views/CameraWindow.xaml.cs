using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace PickandPlace2026.Views
{
    public sealed partial class CameraWindow : Microsoft.UI.Xaml.Window
    {
        // OpenCvSharp's VideoCapture doesn't expose friendly device names the way
        // AForge's FilterInfoCollection did, so devices are found by probing
        // indices and seeing which ones actually open.
        private const int MaxProbedDevices = 5;
        private readonly List<int> _deviceIndexes = new List<int>();

        private VideoCapture? _capture;
        private Thread? _captureThread;
        private volatile bool _capturing = false;

        public CameraWindow()
        {
            InitializeComponent();

            Title = "Camera";
            ConfigureAppWindow();

            Closed += CameraWindow_Closed;

            // Probing runs on a background thread now, and each individual device
            // open gets its own timeout inside ProbeDevices(). cv::VideoCapture with
            // CAP_MSMF is known to hang indefinitely (not just fail, the way DSHOW
            // does) when probing an index with no real camera behind it, and
            // MaxProbedDevices scans a fixed range regardless of how many cameras
            // actually exist. Running that on the UI thread synchronously in the
            // constructor (as this used to) meant a hang on any unpopulated index
            // froze the whole app - every window shares this UI thread - and this
            // window itself never even reached Activate().
            Task.Run(() => ProbeDevices());
        }

        private void ConfigureAppWindow()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new SizeInt32(820, 720));

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
            }

            DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                int x = (displayArea.WorkArea.Width - appWindow.Size.Width) / 2;
                int y = (displayArea.WorkArea.Height - appWindow.Size.Height) / 2;
                appWindow.Move(new PointInt32(x, y));
            }
        }

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        private void ProbeDevices()
        {
            for (int i = 0; i < MaxProbedDevices; i++)
            {
                int index = i;
                VideoCapture? probe = null;
                bool opened = false;

                // The VideoCapture constructor is a blocking native call with no
                // built-in cancellation, so a hang can't be interrupted directly -
                // run it on its own thread and just move on if it doesn't finish
                // within the timeout. An abandoned probe thread is harmless: it
                // isn't touching anything else, and either eventually finishes on
                // its own (in which case the capture below disposes it) or stays
                // blocked forever without affecting the rest of this enumeration.
                Thread probeThread = new Thread(() =>
                {
                    try
                    {
                        probe = new VideoCapture(index, VideoCaptureAPIs.MSMF);
                        opened = probe.IsOpened();
                    }
                    catch
                    {
                        opened = false;
                    }
                });
                probeThread.IsBackground = true;
                probeThread.Start();

                bool finished = probeThread.Join(ProbeTimeout);

                if (finished && opened)
                {
                    _deviceIndexes.Add(index);
                    DispatcherQueue.TryEnqueue(() => camera.Items.Add("Camera " + index));
                }

                if (finished)
                {
                    probe?.Dispose();
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                camera.SelectedIndex = camera.Items.Count > 0 ? 0 : -1;
            });
        }

        private void Button1_Click(object sender, RoutedEventArgs e)
        {
            if (_capturing)
            {
                StopCapture();
                button1.Content = "Start Camera";
            }
            else
            {
                if (camera.SelectedIndex < 0) return;

                StartCapture(_deviceIndexes[camera.SelectedIndex]);
                button1.Content = "Stop";
            }
        }

        private void StartCapture(int deviceIndex)
        {
            _capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.MSMF);
            if (!_capture.IsOpened())
            {
                _capture.Dispose();
                _capture = null;
                return;
            }

            _capturing = true;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true };
            _captureThread.Start();
        }

        private void StopCapture()
        {
            _capturing = false;
            _captureThread?.Join(500);
            _captureThread = null;

            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }

        private void CaptureLoop()
        {
            using (Mat frame = new Mat())
            {
                while (_capturing && _capture != null && _capture.Read(frame))
                {
                    if (frame.Empty()) continue;

                    int totalWidth = frame.Width;
                    int totalHeight = frame.Height;

                    // draw center cross lines on the frame
                    Cv2.Line(frame, new OpenCvSharp.Point(0, totalHeight / 2), new OpenCvSharp.Point(totalWidth, totalHeight / 2), Scalar.White, 1);
                    Cv2.Line(frame, new OpenCvSharp.Point(totalWidth / 2, 0), new OpenCvSharp.Point(totalWidth / 2, totalHeight), Scalar.White, 1);

                    Bitmap bitmap = frame.ToBitmap();

                    // We're on the capture loop thread here, not the UI thread -
                    // marshal the bitmap-to-BitmapImage conversion and the
                    // Image.Source assignment back to the UI thread that owns
                    // CapturedImageBox (same pattern PCBBuilder uses for its own
                    // camera preview).
                    CapturedImageBox.DispatcherQueue.TryEnqueue(async () =>
                    {
                        BitmapImage? bitmapImage = await MakeBitmapImageAsync(bitmap);
                        if (bitmapImage != null)
                        {
                            CapturedImageBox.Source = bitmapImage;
                        }
                        bitmap.Dispose();
                    });
                }
            }
        }

        private static async Task<BitmapImage?> MakeBitmapImageAsync(Bitmap bitmap)
        {
            try
            {
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    bitmap.Save(memoryStream, ImageFormat.Bmp);
                    memoryStream.Position = 0;

                    using (InMemoryRandomAccessStream randomAccessStream = new InMemoryRandomAccessStream())
                    {
                        using (DataWriter writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
                        {
                            writer.WriteBytes(memoryStream.ToArray());
                            await writer.StoreAsync();
                            await writer.FlushAsync();
                            writer.DetachStream();
                        }

                        BitmapImage bitmapImage = new BitmapImage();
                        await bitmapImage.SetSourceAsync(randomAccessStream);
                        return bitmapImage;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private void Camera_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_capturing)
            {
                StopCapture();
                button1.Content = "Start Camera";
            }
        }

        private void CameraWindow_Closed(object sender, WindowEventArgs args)
        {
            StopCapture();
        }
    }
}
