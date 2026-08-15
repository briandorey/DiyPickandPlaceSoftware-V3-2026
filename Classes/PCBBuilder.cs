using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Windows.Storage.Streams;

using System.Drawing.Imaging;

namespace PickandPlace2026.Classes
{
    public class PCBBuilder
    {
        public Board board = new Board();

        DataTable dtLog = new DataTable();
        string _LogFile = "";

        private Components comp = new Components();

        // Not set by the constructor - populated by SetupPCBBuilder(), which
        // runs every time a board is loaded (HomePage), before any build can
        // be started. usbController is additionally refreshed in
        // ActivateBuildProcess() right before a build starts, which is where
        // a still-unconnected USB device is actually detected and rejected -
        // see the comment there.
        private UsbDevice usbController = null!;
        private Kflop kf = null!;
        public KflopLocation kfl = new KflopLocation(0, 100, 100, 0, 0, 0, 0, 0, 0, 0, false, false);

        // Genuinely nullable, not just "not set yet" - HomePage.Bt_Load_Click
        // always passes null here (no camera preview control on that page
        // anymore, see SetupPCBBuilder's comment below), so this stays null for
        // the lifetime of every current caller.
        private Image? img;

        // manual picker selector
        public int currentfeeder = 0;
        int MotorRunLoop = 20;
        // bed settings
        public double NeedleZHeight = 34.9;

        public double dblPCBThickness = 1.6;
        public int FeedRate = 5000;

        public double ClearHeight = 5;

        // nozzle to camera offsets
        public double Nozzle1Xoffset = 0;
        public double Nozzle1Yoffset = 0;

        public double Nozzle2Xoffset = 0;
        public double Nozzle2Yoffset = 0;

        // nozzle sleep times

        public int Nozzel1PickSleep = 150; //50
        public int Nozzel1PlaceSleep = 150;

        public int Nozzel2PickSleep = 300;
        public int Nozzel2PlaceSleep = 300;

        // Intentionally never assigned - camera-assisted pick verification
        // (CheckWithCamera) isn't wired into the build loop yet, so this stays
        // unopened on purpose (see SetupPCBBuilder's comment). Suppressed rather
        // than silenced by actually opening a camera nothing yet uses.
#pragma warning disable CS0649
        VideoCapture? videoCam;
#pragma warning restore CS0649
        private Thread? cameraCaptureThread;
        private volatile bool cameraCapturing = false;
        private VisionProcessing visionLocation = new VisionProcessing();

        // Only ever assigned inside ProcessCapturedFrame (the camera capture
        // thread), so it stays null until at least one frame has come through.
        // CheckWithCamera - the only reader/writer of this field - is not
        // currently wired into the build loop (see SetupPCBBuilder's comment),
        // so this path isn't exercised yet; if it is wired up, the null!
        // below would need revisiting alongside guarding the early
        // "outstr.Loc1X = 0" reset in CheckWithCamera that runs before
        // StartCameraCapture has produced a first frame.
        VisionLocationResult outstr = null!;
        private bool VisionCorrectionNeeded = false;

        private readonly BackgroundWorker backgroundWorkerBuildPCB = new BackgroundWorker();

        /// <summary>
        /// Raised when an error occurs, including from background threads (the build
        /// worker and the camera capture callback). This class cannot show a
        /// ContentDialog itself - it has no XamlRoot and may not be on the UI thread -
        /// so subscribe to this event from the Page/ViewModel that owns the UI and
        /// marshal to the UI thread there (e.g. via DispatcherQueue.TryEnqueue) to
        /// display it.
        /// </summary>
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>
        /// Raised at each step of a build so the status bar can show live
        /// progress - diagnostic in origin (the build loop was dying/hanging
        /// with zero visible signal, and Debug.WriteLine output requires the
        /// debugger attached, which itself changes behavior around the known
        /// Trajectory Planner warning), but useful on its own regardless.
        /// </summary>
        public event EventHandler<string>? BuildProgress;

        private void RaiseProgress(string message)
        {
            FileLog.Write(message);
            BuildProgress?.Invoke(this, message);
        }

        private void RaiseError(string message)
        {
            FileLog.Write("ERROR: " + message);
            ErrorOccurred?.Invoke(this, message);
        }

        public void SetupPCBBuilder(Kflop kflop, UsbDevice usb, Board boardData, string LogFile,
            Image? imgref, VideoCapture? videoBaseCam)
        {
            try
            {
                _LogFile = LogFile;
                board = boardData;
                kf = kflop;
                usbController = usb;
                img = imgref;

                // dtLog.ReadXml(LogFile) used to be here, reading the feeder-usage
                // log HomePage just wrote via a bare WriteXml(_logFile) call (no
                // embedded schema). DataTable.ReadXml can't infer a schema from
                // schema-less XML into an empty (zero-column) DataTable, so this
                // threw InvalidOperationException("DataTable does not support
                // schema inference from Xml.") on every single board load - caught
                // by this try/catch, which meant every line after it, including
                // the DoWork/RunWorkerCompleted subscriptions below, never ran.
                // ActivateBuildProcess's checks don't depend on that subscription,
                // so Start would still call RunWorkerAsync() successfully - just
                // against a worker with no DoWork handler attached, so it did
                // nothing and returned instantly: no motion, no error, nothing to
                // catch. dtLog's only other reader, UpdateComponents(), is never
                // called anywhere in this codebase, so this line was pure
                // liability with no upside - removed rather than fixing the
                // schema mismatch, since nothing needs dtLog populated.
                //  videoCam = videoBaseCam;

                // camera-assisted pick verification (CheckWithCamera) is not wired
                // into the build loop yet - videoCam is intentionally left unopened

                // setup worker methods - SetupPCBBuilder runs again on every board
                // load, not just once, so unsubscribe first or a second load makes
                // DoWork/RunWorkerCompleted fire twice (once per prior subscription)
                // on the next build.
                backgroundWorkerBuildPCB.DoWork -= Worker_DoWork;
                backgroundWorkerBuildPCB.DoWork += Worker_DoWork;
                backgroundWorkerBuildPCB.RunWorkerCompleted -= Worker_RunWorkerCompleted;
                backgroundWorkerBuildPCB.RunWorkerCompleted += Worker_RunWorkerCompleted;
                backgroundWorkerBuildPCB.WorkerSupportsCancellation = true;
            }
            catch (Exception e)
            {
                RaiseError("SetupPCBBuilder Error: " + e.ToString());
            }
        }

        private void Worker_DoWork(object? sender, DoWorkEventArgs e)
        {
            // Logged before anything else can possibly throw, so the log file
            // alone proves whether RunWorkerAsync ever actually got this handler
            // running at all, independent of the InfoBar/DispatcherQueue path.
            FileLog.Write("Worker_DoWork entered");
            try
            {
            // run all background tasks here
            BackgroundWorker? worker = sender as BackgroundWorker;
            List<BoardComponent> pickedComponents = board.Components.Where(c => c.Pick).ToList();
            int currentrow = 0;
            int totalrows = pickedComponents.Count;

            AppSettings settings = ((App)Application.Current).Settings;
            Nozzle1Xoffset = settings.Nozzle1Xoffset;
            Nozzle1Yoffset = settings.Nozzle1Yoffset;
            Nozzle2Xoffset = settings.Nozzle2Xoffset;
            Nozzle2Yoffset = settings.Nozzle2Yoffset;

            // MoveSingleFeed's bool result was never checked anywhere below - an
            // out-of-bounds target, a lost hardware connection, or any other
            // failure it catches internally would silently return false and the
            // loop would carry on regardless, straight into the
            // GetFeederReadyStatus/CheckChipMotorRunning wait loops further down,
            // which then spin forever waiting for a physical state the failed
            // move never produced. That's the "build started, then nothing"
            // symptom: no error, no motion, no way out except killing the app.
            // Move() below checks the result and aborts the whole build on the
            // first failure instead.
            bool aborted = false;
            bool Move(double speed, double x, double y, double z, double a, double b, double c)
            {
                if (aborted) return false;
                if (!kf.MoveSingleFeedOnUIThread(speed, x, y, z, a, b, c))
                {
                    aborted = true;
                    RaiseError("Build stopped: a move failed (see the Kflop error above for details).");
                }
                return !aborted;
            }

            // Bounded wait for a physical status flag - returns false (and aborts
            // the build) if cancelled or if the flag never comes true, instead of
            // spinning here forever.
            bool WaitFor(Func<bool> condition, string timeoutMessage)
            {
                int waitedMs = 0;
                while (!condition())
                {
                    if (backgroundWorkerBuildPCB.CancellationPending)
                    {
                        e.Cancel = true;
                        aborted = true;
                        return false;
                    }
                    Thread.Sleep(10);
                    waitedMs += 10;
                    if (waitedMs > 5000)
                    {
                        aborted = true;
                        RaiseError("Build stopped: " + timeoutMessage);
                        return false;
                    }
                }
                return true;
            }

            RaiseProgress($"Build: {totalrows} component(s) selected to place");

            if (totalrows > 0)
            {
                double pcbHeight = board.BoardInfo.BoardHeight;

                double feedrate = 100;

                while (currentrow < totalrows && !aborted)
                {
                    if (backgroundWorkerBuildPCB.CancellationPending)
                    {
                        e.Cancel = true;
                        break;
                    }

                    RaiseProgress($"Build: row {currentrow + 1}/{totalrows} - computing target");

                    BoardComponent currentComponent = pickedComponents[currentrow];
                    string currentCode = currentComponent.ComponentCode.ToString();

                    kfl.PlacementNozzle = currentComponent.PlacementNozzle;
                    kfl.PickSpeed = feedrate;
                    kfl.PlaceSpeed = comp.GetPlaceSpeed(currentCode, feedrate);

                    kfl.FeederX = CalcXLocation(comp.GetFeederX(currentCode), kfl.PlacementNozzle);
                    kfl.FeederY = CalcYLocation(comp.GetFeederY(currentCode), kfl.PlacementNozzle);
                    kfl.FeederHeight = comp.GetFeederHeight(currentCode);

                    kfl.PlaceHeight = comp.GetPlacementHeight(currentCode) - pcbHeight;

                    kfl.TapeFeeder = comp.GetComponentTapeFeeder(currentCode);

                    kfl.PlaceX = CalcXLocation(currentComponent.PlacementX, kfl.PlacementNozzle);
                    kfl.PlaceY = CalcYLocation(currentComponent.PlacementY, kfl.PlacementNozzle);
                    kfl.PlaceRotation = currentComponent.PlacementRotate;

                    kfl.VerifyCamera = comp.GetComponentVerifywithCamera(currentCode);

                    if (currentrow == 0)
                    {
                        SetFeederOutputs(comp.GetFeederID(currentCode)); // send feeder to position
                    }

                    RaiseProgress($"Build: row {currentrow + 1}/{totalrows} - moving to feeder X={kfl.FeederX:F2} Y={kfl.FeederY:F2}");
                    if (!Move(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, ClearHeight, ClearHeight, 0, 0)) break;

                    if (comp.GetComponentTapeFeeder(currentCode))
                    {
                        RaiseProgress($"Build: row {currentrow + 1}/{totalrows} - waiting for feeder ready");
                        if (!WaitFor(usbController.GetFeederReadyStatus, "feeder never reported ready.")) break;
                        Thread.Sleep(50);
                        if (kfl.PlacementNozzle == 1)
                        {
                            // use picker 1
                            RaiseProgress($"Build: row {currentrow + 1}/{totalrows} - picking (nozzle 1)");
                            if (!Move(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, kfl.FeederHeight, ClearHeight, 0, 0)) break;
                            Thread.Sleep(Nozzel1PickSleep);
                            // go down and turn on suction
                            usbController.SetVAC1(true);
                            Thread.Sleep(Nozzel1PickSleep);
                            if (!Move(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, ClearHeight, ClearHeight, 0, 0)) break;
                        }
                        else
                        {
                            // nozzle 2 on tape feeder
                            RaiseProgress($"Build: row {currentrow + 1}/{totalrows} - picking (nozzle 2, tape)");
                            if (!Move(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, ClearHeight, kfl.FeederHeight, 0, 0)) break;
                            Thread.Sleep(Nozzel2PickSleep);
                            // go down and turn on suction
                            usbController.SetVAC2(true);
                            Thread.Sleep(Nozzel2PickSleep);
                            if (!Move(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, ClearHeight, ClearHeight, 0, 0)) break;
                        }
                    }
                    else
                    {
                        // use picker 2
                        RaiseProgress($"Build: row {currentrow + 1}/{totalrows} - waiting for chip motor");
                        if (!WaitFor(() => !usbController.CheckChipMotorRunning(), "chip feeder motor never finished.")) break;
                        RaiseProgress($"Build: row {currentrow + 1}/{totalrows} - picking (nozzle 2, chip)");
                        if (!Move(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, ClearHeight, kfl.FeederHeight, 0, 0)) break;
                        Thread.Sleep(Nozzel2PickSleep); // 200

                        usbController.SetVAC2(true);
                        Thread.Sleep(Nozzel2PickSleep); // 300
                        if (!Move(kfl.PickSpeed, kfl.FeederX, kfl.FeederY, ClearHeight, ClearHeight, 0, 0)) break;
                    }
                    // send picker to pick next item
                    if (currentrow >= 0 && (currentrow + 1) < totalrows)
                    {
                        Thread.Sleep(100);
                        Thread.Sleep(100);

                        SetFeederOutputs(comp.GetFeederID(pickedComponents[currentrow + 1].ComponentCode.ToString())); // send feeder to position
                    }

                    // rotate head and place component
                    RaiseProgress($"Build: row {currentrow + 1}/{totalrows} - placing at X={kfl.PlaceX:F2} Y={kfl.PlaceY:F2}");
                    if (comp.GetComponentTapeFeeder(currentCode) && (kfl.PlacementNozzle == 1))
                    {
                        if (!Move(kfl.PlaceSpeed, kfl.PlaceX, kfl.PlaceY, ClearHeight, ClearHeight, 0, kfl.PlaceRotation)) break;
                        if (!Move(kfl.PlaceSpeed, kfl.PlaceX, kfl.PlaceY, kfl.PlaceHeight, ClearHeight, 0, kfl.PlaceRotation)) break;

                        Thread.Sleep(Nozzel1PlaceSleep);
                        usbController.SetVAC1(false);
                        Thread.Sleep(Nozzel1PlaceSleep);
                        if (!Move(kfl.PlaceSpeed, kfl.PlaceX, kfl.PlaceY, ClearHeight, ClearHeight, 0, kfl.PlaceRotation)) break;
                    }
                    else
                    {
                        // use picker 2  CalcXwithNeedleSpacing
                        if (!Move(kfl.PlaceSpeed, kfl.PlaceX, kfl.PlaceY, ClearHeight, ClearHeight, kfl.PlaceRotation, 0)) break;
                        Thread.Sleep(Nozzel2PlaceSleep);
                        if (!Move(kfl.PlaceSpeed, kfl.PlaceX, kfl.PlaceY, ClearHeight, kfl.PlaceHeight, kfl.PlaceRotation, 0)) break;

                        // go down and turn off suction
                        Thread.Sleep(Nozzel2PlaceSleep);
                        usbController.SetVAC2(false);
                        Thread.Sleep(Nozzel2PlaceSleep);
                        if (!Move(kfl.PlaceSpeed, kfl.PlaceX, kfl.PlaceY, ClearHeight, ClearHeight, kfl.PlaceRotation, 0)) break;
                    }

                    RaiseProgress($"Build: row {currentrow + 1}/{totalrows} - done");
                    currentrow++;
                }
                if (!aborted && !e.Cancel)
                {
                    // move near to home point
                    RaiseProgress("Build: all rows done - moving near home point");
                    kf.MoveSingleFeedOnUIThread(kfl.PickSpeed, 10, 10, 10, 10, 0, 0);
                }
            }
            else
            {
                RaiseError("No components selected to place - check components in the grid or use Check All.");
            }
            RaiseProgress("Build: resetting feeder and running vibration motor");
            usbController.SetResetFeeder();
            usbController.RunVibrationMotor(MotorRunLoop);

            // A standalone Home click (idle controller) homes cleanly in ~2s.
            // Immediately after a build's CoordMotion.StraightFeed/
            // WaitForSegmentsFinished/FlushSegments sequence, the same
            // RunHomeAll() call has been observed getting Z/A stuck at
            // flags=0 (motors audibly straining, not actually turning) - the
            // one difference between the two cases is that this call follows
            // active coordinate motion instead of an idle controller.
            // RunHomeAll() already calls SetAlltoZero() (CoordMotion.Abort/
            // ClearAbort) right before starting the homing thread, but that
            // may not give the interpreter/hardware enough time to fully
            // settle before HomePickAndPlace.c takes direct control of the
            // same axes. Testing whether a short settle delay here - only on
            // this post-build path, not inside RunHomeAll() itself, so the
            // already-working standalone Home button is untouched - avoids
            // that.
            RaiseProgress("Build: letting controller settle before final home");
            Thread.Sleep(500);

            // RunHomeAll() re-homes Z/A along with X/Y every time it's called, so
            // this doesn't prevent them dipping back down while their own homing
            // step runs - only HomePickAndPlace.c changing that would. This just
            // makes sure they're not already sitting low going into it, using the
            // same bounds-checked MoveSingleFeed path the whole build already
            // relies on rather than raw axis jogging.
            RaiseProgress("Build: raising Z/A to Z=10 A=10 before final home");
            kf.MoveSingleFeedOnUIThread(kfl.PickSpeed, 10, 10, 10, 10, 0, 0);

            RaiseProgress("Build: running final home");
            kf.RunHomeAll();
            RaiseProgress("Build: finished");

            //dtLog.WriteXml(_LogFile);
            }
            catch (Exception ex)
            {
                FileLog.Write("EXCEPTION in Worker_DoWork: " + ex);
                throw;
            }
        }

        private void Worker_RunWorkerCompleted(object? sender,
                                               RunWorkerCompletedEventArgs e)
        {
            FileLog.Write($"Worker_RunWorkerCompleted: Error={e.Error}, Cancelled={e.Cancelled}");

            // Any exception thrown during Worker_DoWork (e.g. a null usbController/
            // Kflop reference, or a bad row value) otherwise lands here silently -
            // BackgroundWorker catches it and stores it on e.Error instead of
            // letting it propagate, so without this the build just stops with no
            // motor movement and no visible error at all.
            if (e.Error != null)
            {
                RaiseError("Build failed: " + e.Error.Message);
            }
        }

        public KflopLocation CheckWithCamera(KflopLocation kfl, Kflop kf, int nozzle, UsbDevice usbController, Image imgref)
        {
            double CameraX = 295.4;
            double CameraY = 150.2;

            double CameraX2 = 263.5;
            double CameraY2 = 150.2;

            double PlaceX = kfl.PlaceX;
            double PlaceY = kfl.PlaceY;
            double PlaceRotation = kfl.PlaceRotation;

            int retrycounter = 0;

            img = imgref;
            outstr.Loc1X = 0;
            StartCameraCapture();

            if (nozzle == 1)
            {
                kf.MoveSingleFeed(kfl.PlaceSpeed, CameraX, CameraY, ClearHeight, ClearHeight, 0, kfl.PlaceRotation);
                usbController.SetHeadCameraLED(true);
            }
            else
            {
                kf.MoveSingleFeed(kfl.PlaceSpeed, CameraX2, CameraY2, ClearHeight, ClearHeight, kfl.PlaceRotation, 0);
                usbController.SetHeadCameraLED(true);
            }
            Thread.Sleep(2000);
            // verify camera image if needed

            // loop until part has correct rotation
            while (VisionCorrectionNeeded && retrycounter < 100)
            {
                if (outstr.OffsetX < 0.5 && outstr.OffsetY < 0.5)
                {
                    PlaceX = PlaceX + outstr.OffsetX;
                    PlaceY = PlaceY + outstr.OffsetY;
                    PlaceRotation = PlaceRotation + outstr.LocAngle;

                    VisionCorrectionNeeded = true;
                }
                else
                {
                    if (nozzle == 1)
                    {
                        PlaceX = PlaceX + outstr.OffsetX;
                        PlaceY = PlaceY + outstr.OffsetY;
                        PlaceRotation = PlaceRotation + outstr.LocAngle;
                        CameraX = CameraX + outstr.OffsetX;
                        CameraY = CameraY + outstr.OffsetY;

                        kf.MoveSingleFeed(kfl.PlaceSpeed, CameraX, CameraY, ClearHeight, ClearHeight, 0, PlaceRotation);
                    }
                    else
                    {
                        PlaceX = PlaceX + outstr.OffsetX;
                        PlaceY = PlaceY + outstr.OffsetY;
                        PlaceRotation = PlaceRotation + outstr.LocAngle;
                        CameraX2 = CameraX2 + outstr.OffsetX;
                        CameraX2 = CameraX2 + outstr.OffsetY;

                        kf.MoveSingleFeed(kfl.PlaceSpeed, CameraX2, CameraY2, ClearHeight, ClearHeight, kfl.PlaceRotation, 0);
                    }
                    VisionCorrectionNeeded = false;
                }
                outstr.Loc1X = 0;
                Thread.Sleep(1000);
                retrycounter++;
            }

            usbController.SetHeadCameraLED(false);
            StopCameraCapture();
            return kfl;
        }

        /// <summary>
        /// Starts a background thread pulling frames from videoCam (OpenCvSharp is
        /// pull-based via VideoCapture.Read, unlike AForge's push-based NewFrame
        /// event) and running each one through ProcessCapturedFrame. No-op if
        /// videoCam hasn't been opened.
        /// </summary>
        private void StartCameraCapture()
        {
            if (videoCam == null || !videoCam.IsOpened() || cameraCapturing) return;

            cameraCapturing = true;
            cameraCaptureThread = new Thread(CameraCaptureLoop) { IsBackground = true };
            cameraCaptureThread.Start();
        }

        private void StopCameraCapture()
        {
            cameraCapturing = false;
            cameraCaptureThread?.Join(500);
            cameraCaptureThread = null;
        }

        private void CameraCaptureLoop()
        {
            using (Mat frame = new Mat())
            {
                while (cameraCapturing && videoCam != null && videoCam.Read(frame))
                {
                    if (frame.Empty()) continue;
                    ProcessCapturedFrame(frame);
                }
            }
        }

        private void ProcessCapturedFrame(Mat frame)
        {
            // Captured into a local rather than read from the "img" field inside
            // the lambda below - a null-check on the field itself doesn't narrow
            // its type inside a closure, since the field could theoretically
            // change before the closure runs.
            Image? currentImg = img;
            if (currentImg == null) return;

            try
            {
                System.Drawing.Bitmap image = frame.ToBitmap();

                visionLocation.SetCurrentImage(image);
                visionLocation.ApplyConvertToGrayscale();
                //visionLocation.ApplySobelEdgeFilter();
                visionLocation.ApplyCannyEdgeDetector();
                outstr = visionLocation.LocateObjects();

                // We're on the capture loop thread here, not the UI thread.
                // DispatcherQueue.TryEnqueue marshals the UI update (and the async
                // bitmap conversion) back onto the UI thread that owns "img".
                System.Drawing.Bitmap? currentFrame = visionLocation.GetCurrentImage();
                if (currentFrame == null) return;

                currentImg.DispatcherQueue.TryEnqueue(async () =>
                {
                    BitmapImage? bitmapImage = await MakeBitmapImageAsync(currentFrame);
                    if (bitmapImage != null)
                    {
                        currentImg.Source = bitmapImage;
                    }
                });
            }
            catch (Exception ex)
            {
                RaiseError(ex.ToString());
            }
        }
       
        public void UpdateComponents(string componentcode)
        {
            foreach (DataRow row in dtLog.Rows)
            {
                if (row["ComponentCode"].ToString() == componentcode)
                {
                    row["Placed"] = Int32.Parse(row["Placed"].ToString() ?? "0") + 1;
                }
            }
        }

        public double CalcXLocation(double val, int nozzle)
        {
            if (nozzle.Equals(1))
            {
                return val - Nozzle1Xoffset;
            }
            else
            {
                return val - Nozzle2Xoffset;
            }
        }

        public double CalcYLocation(double val, int nozzle)
        {
            if (nozzle.Equals(1))
            {
                return val - Nozzle1Yoffset;
            }
            else
            {
                return val - Nozzle2Yoffset;
            }
        }

        public void SetFeederOutputs(int feedercommand)
        {
            usbController.SetGotoFeeder(Byte.Parse(feedercommand.ToString()));

            // check if on main feeder rack
            if (feedercommand == 98)
            {
                // command set, now toggle interupt pin
                usbController.SetResetFeeder();
            }
            if (feedercommand >= 20 && feedercommand < 30)
            {
                usbController.RunVibrationMotor(MotorRunLoop);
            }
        }

        // Returns whether the build actually started, so the caller can avoid
        // showing "Build started" when it didn't - HomePage.Bt_Start_Click used to
        // show that unconditionally right after calling this, which silently
        // overwrote whatever rejection error this method had just raised (both go
        // through the same DispatcherQueue.TryEnqueue-based status pipeline, so
        // the later "Build started" call always wins the race).
        public bool ActivateBuildProcess(int motorruntime, bool HighSpeedMode)
        {
            FileLog.Write($"ActivateBuildProcess entered: motorruntime={motorruntime}, HighSpeedMode={HighSpeedMode}, kf.IsReady={kf.IsReady}, IsBusy={backgroundWorkerBuildPCB.IsBusy}");

            // If the KFLOP init program never loaded, axes were never Enable()d
            // and MotionParams was never configured - but MoveSingleFeed's
            // StraightFeed/WaitForSegmentsFinished calls still complete without
            // error against the coordinate interpreter regardless, so the whole
            // build loop would otherwise run through as if every move succeeded
            // while nothing physically moves. Check up front instead of letting
            // that play out silently.
            if (!kf.IsReady)
            {
                RaiseError("Cannot start build: Kflop is not ready (see the earlier initialization error in the status bar).");
                return false;
            }

            // usbController was captured once, back when SetupPCBBuilder ran (at
            // Load PCB File time) - if the board was loaded before the USB device
            // finished attaching asynchronously at startup, this stayed null for
            // the rest of the session even after HomePage's own reference later
            // updated, since nothing here ever refreshed it. Every usbController
            // call inside Worker_DoWork's feeder-ready/chip-motor wait loops would
            // then throw a NullReferenceException almost immediately - caught by
            // BackgroundWorker into e.Error, but before ever reaching the loop's
            // real motion or its RunHomeAll() cleanup at the end, which is exactly
            // what "Setting All to Zero" with no "Starting Home" afterward shows.
            // Refresh from App right before actually starting, when USB has had
            // far longer to attach than it did at Load time.
            usbController = ((App)Application.Current).GetUSBDevice();
            if (usbController == null)
            {
                RaiseError("Cannot start build: USB controller is not connected.");
                return false;
            }

            if (backgroundWorkerBuildPCB.IsBusy)
            {
                RaiseError("Cannot start build: a build is already running.");
                return false;
            }

            if (HighSpeedMode)
            {
                Nozzel1PickSleep = 50; //50
                Nozzel1PlaceSleep = 50;

                Nozzel2PickSleep = 75;
                Nozzel2PlaceSleep = 75;
            }
            else
            {
                Nozzel1PickSleep = 150; //50
                Nozzel1PlaceSleep = 150;

                Nozzel2PickSleep = 300;
                Nozzel2PlaceSleep = 300;
            }

            kf.SetAlltoZero();
            MotorRunLoop = motorruntime;

            FileLog.Write("ActivateBuildProcess: calling RunWorkerAsync()");
            backgroundWorkerBuildPCB.RunWorkerAsync();
            FileLog.Write("ActivateBuildProcess: RunWorkerAsync() returned, IsBusy=" + backgroundWorkerBuildPCB.IsBusy);
            return true;
        }

        public void CancelBuildProcess()
        {
            if (backgroundWorkerBuildPCB.WorkerSupportsCancellation)
            {
                backgroundWorkerBuildPCB.CancelAsync();
            }
        }

        /// <summary>
        /// Converts a System.Drawing.Bitmap into a WinUI 3 BitmapImage suitable for
        /// assigning to an Image control's Source property. Must be awaited/run on
        /// the UI thread, since BitmapImage is a XAML DispatcherQueue-bound object.
        /// Replaces the old WPF-style BeginInit/StreamSource/EndInit/Freeze pattern,
        /// which does not exist in WinUI 3.
        /// </summary>
        private async Task<BitmapImage?> MakeBitmapImageAsync(System.Drawing.Bitmap bitmap)
        {
            if (bitmap == null) return null;

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
            catch (Exception ex)
            {
                RaiseError("MakeBitmapImageAsync: " + ex.ToString());
                return null;
            }
        }
    }
}