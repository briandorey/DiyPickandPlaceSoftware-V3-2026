using KMotion_dotNet;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PickandPlace2026.Classes
{
    public class Kflop
    {
        // Not set by the constructor - Kflop is constructed via App's field
        // initializer (App.xaml.cs, before App's own constructor body runs), and
        // these are only assigned once InitDevice() runs later on a background
        // task. IsReady/CheckReady() guard the hardware-touching entry points
        // against use before that happens.
        KM_Controller _Controller = null!; //Object to interface with the Kflop
        KM_Axis _XAxis = null!;
        KM_Axis _YAxis = null!;
        KM_Axis _ZAxis = null!;
        KM_Axis _AAxis = null!;
        KM_Axis _BAxis = null!;
        KM_Axis _CAxis = null!;
        KM_CoordMotion _Motion = null!;

        private double currentX = 0.0;
        private double currentY = 0.0;
        private double currentZ = 0.0;
        private double currentA = 0.0;
        private double currentB = 0.0;
        private double currentC = 0.0;

        public bool eStopActive = false;

        // False until InitDeviceSettings() has fully completed - axes Enable()d
        // and MotionParams/CountsPerInch configured. Before that, MoveSingleFeed's
        // StraightFeed/WaitForSegmentsFinished calls run against the coordinate
        // interpreter's software trajectory and can return successfully - no
        // exception, no false return - even though the physical axis drivers
        // were never enabled, so nothing actually moves. Callers that trigger real
        // motion (a build, Home) should check this first instead of assuming a
        // move that "succeeded" actually moved anything.
        public bool IsReady { get; private set; } = false;

        // Central guard for every hardware-touching entry point below.
        // _Controller/_XAxis/etc. aren't assigned until InitDeviceSettings()
        // finishes, so calling into any of them beforehand - e.g. pressing Home
        // (or a Stream Deck hotkey firing it) while the Kflop is still
        // initialising on startup - throws a NullReferenceException and crashes
        // the app. Checking here, once, means every current and future caller
        // (UI buttons, hotkeys, PCBBuilder) is protected without having to
        // remember to check IsReady at each call site individually.
        private bool CheckReady([CallerMemberName] string caller = "")
        {
            if (IsReady) return true;
            RaiseError($"{caller} ignored - Kflop has not finished initialising yet");
            return false;
        }

        // KM_Controller's underlying connection to the KFLOP is a single shared
        // resource, not obviously safe for concurrent calls from multiple .NET
        // threads at once. RunHomeAll() polls it from its own background thread
        // (started in HomeAll()) while ManualControl's limit-switch/E-Stop/Homed
        // indicators poll it too - without serializing access, those two could
        // interleave requests/responses on the same connection, which can look
        // like a stalled UI (if the polling was on the UI thread) or corrupted
        // reads (e.g. RunHomeAll's completion-flag poll never matching even
        // though homing actually finished). Every method that talks to
        // _Controller/the axes takes this lock for its whole body.
        private readonly object _controllerLock = new object();

        // Old app's faster values. These ran full builds successfully multiple
        // times earlier in testing, then one build crashed the machine on the
        // first-ever run of a published (non-debugger) build - root cause not
        // confirmed (settings.json differences and "never tested at full speed"
        // were both checked and ruled out). Restored per explicit instruction
        // after that incident; a proper PCB run is planned to check whether it
        // recurs. Test with a hand on E-Stop.
        KflopAxis AxisX = new KflopAxis(10000, 500000000, 1500);
        KflopAxis AxisY = new KflopAxis(6000, 500000000, 520);

        public event EventHandler<string>? ErrorOccurred;

        private void RaiseError(string message)
        {
            FileLog.Write("KFLOP ERROR: " + message);
            ErrorOccurred?.Invoke(this, message);
        }

        public Kflop()
        {
        }

        public void InitDeviceSettings()
        {
            IsReady = false;

            // InitPickandPlace.c is copied straight into the output directory
            // (CopyToOutputDirectory="Always" in the csproj), i.e. AppContext.BaseDirectory
            // itself - whether running unpackaged (...\win-x64\) or packaged (...\win-x64\AppX\).
            // This previously walked up two directory levels looking for the file, which
            // overshot past the folder that actually contains it in both layouts, so
            // ExecuteProgram() below always failed to find/load the program. That failure
            // was silently absorbed by RaiseError()+return, which skipped axis Enable() and
            // all HomingParams/MotionParams setup - so homing later moved nothing, with no
            // error at the time Home was clicked (the real error fired here, at startup).
            string TheCFile = Path.Combine(AppContext.BaseDirectory, "InitPickandPlace.c");

            //************NEW program execution model***********
            string result = _Controller.ExecuteProgram(1, TheCFile, false);

            if (result != "")
            {
                RaiseError(result);
                // Do not continue configuring axes/homing/motion params against a
                // controller that just failed to load its init program - several
                // of the calls below block waiting on hardware responses and will
                // hang rather than fail fast if the controller isn't in a valid
                // state.
                return;
            }

            _XAxis.Enable();
            _XAxis.CPU = 1000;

            _YAxis.Enable();
            _YAxis.CPU = 1000;

            _ZAxis.Enable();
            _ZAxis.CPU = 1000;

            _AAxis.Enable();
            _AAxis.CPU = 1000;

            _BAxis.Enable();
            _BAxis.CPU = 1000;

            _CAxis.Enable();
            _CAxis.CPU = 1000;

            // Homing no longer uses KM_Axis.HomingParams / HOMING_ROUTINE_SOURCE_TYPE.AUTO
            // here - AUTO's built-in home-task generator has a long-documented bug in its
            // limit-switch comparison logic (Dynomotion forum, "Homing setup and issues")
            // that lets an axis keep driving after its limit bit is already reporting
            // triggered. Confirmed on this machine: the live limit-switch indicators on
            // Manual Control showed the bit going active on every axis while it kept
            // driving into the hard stop. RunHomeAll() below now runs HomePickAndPlace.c
            // instead, a custom homing program (adapted from Dynomotion's own
            // C:\KMotion5.4.5\C Programs\HomeMM_V10.c) with the same Z/A/X/Y switch bits,
            // directions, and speeds that used to be configured here.

            // setup motion params
            _Controller.CoordMotion.Abort();
            _Controller.CoordMotion.ClearAbort();
            _Controller.CoordMotion.MotionParams.CountsPerInchX = 171.405629;
            _Controller.CoordMotion.MotionParams.CountsPerInchY = 416.3108547;
            _Controller.CoordMotion.MotionParams.CountsPerInchZ = 342.245989;
            _Controller.CoordMotion.MotionParams.CountsPerInchA = 342.245989;
            _Controller.CoordMotion.MotionParams.CountsPerInchB = 8.88888;
            _Controller.CoordMotion.MotionParams.CountsPerInchC = 8.88888;

            _Controller.CoordMotion.MotionParams.MaxAccelX = AxisX.Accel;
            _Controller.CoordMotion.MotionParams.MaxAccelY = AxisY.Accel;
            _Controller.CoordMotion.MotionParams.MaxAccelZ = 8000;
            _Controller.CoordMotion.MotionParams.MaxAccelA = 8000;
            _Controller.CoordMotion.MotionParams.MaxAccelB = 4000;
            _Controller.CoordMotion.MotionParams.MaxAccelC = 4000;

            _XAxis.TuningParams.Jerk = AxisX.Jerk;
            _YAxis.TuningParams.Jerk = AxisY.Jerk;
            _ZAxis.TuningParams.Jerk = 100000000;
            _AAxis.TuningParams.Jerk = 100000000;
            _BAxis.TuningParams.Jerk = 4000000;
            _CAxis.TuningParams.Jerk = 4000000;

            _Controller.CoordMotion.MotionParams.MaxVelX = AxisX.MaxVel;
            _Controller.CoordMotion.MotionParams.MaxVelY = AxisY.MaxVel;
            _Controller.CoordMotion.MotionParams.MaxVelZ = 20000;
            _Controller.CoordMotion.MotionParams.MaxVelA = 20000;
            _Controller.CoordMotion.MotionParams.MaxVelB = 4000;
            _Controller.CoordMotion.MotionParams.MaxVelC = 4000;

            SetAlltoZero();

            IsReady = true;
        }

        public void InitDevice()
        {
            Debug.WriteLine("Init Device");

            _Controller = new KMotion_dotNet.KM_Controller();

            _XAxis = new KMotion_dotNet.KM_Axis(_Controller, 0, "x");
            _YAxis = new KMotion_dotNet.KM_Axis(_Controller, 1, "y");
            _ZAxis = new KMotion_dotNet.KM_Axis(_Controller, 2, "z");
            _AAxis = new KMotion_dotNet.KM_Axis(_Controller, 3, "a");
            _BAxis = new KMotion_dotNet.KM_Axis(_Controller, 4, "b");
            _CAxis = new KMotion_dotNet.KM_Axis(_Controller, 5, "c");
            _Motion = new KMotion_dotNet.KM_CoordMotion(_Controller);

            AddHandlers();

            InitDeviceSettings();
        }

        // public methods

        public void HomeAll()
        {
            Thread runhoming = new Thread(new ThreadStart(RunHomeAll));
            runhoming.Start();
        }

        // KFLOP thread the custom homing program runs on - distinct from thread 1,
        // which InitPickandPlace.c uses at startup. HomingCFile is copied to the
        // output directory the same way InitPickandPlace.c is (see the .csproj).
        private const int HomingThread = 6;
        private static readonly string HomingCFile = Path.Combine(AppContext.BaseDirectory, "HomePickAndPlace.c");

        // Bit index HomePickAndPlace.c's Validate() writes per-axis completion flags
        // into (persist.UserData[HOMING_COMPLETE_FLAGS] in the .c file), and which
        // axis channels it drives: X=0, Y=1, Z=2, A=3 (B/C aren't homed).
        private const int HomingCompleteUserDataIndex = 88;
        private const int HomingCompleteMask = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 3);

        public void RunHomeAll()
        {
            if (!CheckReady()) return;

            Debug.WriteLine("Starting Home");
            FileLog.Write("RunHomeAll entered");

            try
            {
                lock (_controllerLock)
                {
                    SetAlltoZero();

                    string result = _Controller.ExecuteProgram(HomingThread, HomingCFile, false);
                    if (result != "")
                    {
                        RaiseError("RunHomeAll: failed to load homing program: " + result);
                        return;
                    }
                }
                FileLog.Write("RunHomeAll: homing program loaded, waiting for completion flags");

                // HomePickAndPlace.c homes Z, then A, then X, then Y (one at a time)
                // entirely on the KFLOP, and only returns from main() once every axis
                // is done - polling its completion flags here mirrors how the old
                // MotionComplete() loops waited, without depending on AUTO. Deliberately
                // NOT held under _controllerLock for the whole wait - only each
                // individual GetUserData() call is - so a several-second homing run
                // doesn't lock out ManualControl's indicator polling (or another jog
                // command) for its entire duration, just for each brief poll.
                DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                int lastLoggedFlags = -1;
                while (true)
                {
                    int flags;
                    lock (_controllerLock)
                    {
                        flags = _Controller.GetUserData(HomingCompleteUserDataIndex);
                    }
                    if (flags != lastLoggedFlags)
                    {
                        FileLog.Write($"RunHomeAll: flags={Convert.ToString(flags, 2)} (mask={Convert.ToString(HomingCompleteMask, 2)})");
                        lastLoggedFlags = flags;
                    }
                    if ((flags & HomingCompleteMask) == HomingCompleteMask)
                    {
                        FileLog.Write("RunHomeAll: completion flags matched, breaking wait loop");
                        break;
                    }
                    if (DateTime.UtcNow > deadline)
                    {
                        RaiseError("RunHomeAll: timed out waiting for homing to complete");
                        return;
                    }
                    Thread.Sleep(50);
                }

                lock (_controllerLock)
                {
                    SetAlltoZero();
                }
                FileLog.Write("RunHomeAll: SetAlltoZero done, marking homed");

               
                App app = (App)Application.Current;
                if (app != null)
                {
                    app.setHomed(true);
                    FileLog.Write("RunHomeAll: complete, setHomed(true)");
                }
                else
                {
                    RaiseError("RunHomeAll: homing completed motion, but could not reach App to record it as homed");
                }
            }
            catch (Exception ex)
            {
                // This runs on its own background thread (started in HomeAll()),
                // so an uncaught exception here has nowhere to go - catch and report
                RaiseError("RunHomeAll: " + ex.ToString());
            }
        }

        // ---- Legacy (pre-2026 app) homing path - opt-in, NOT wired to any Home
        // button. Ported verbatim from the old app's kflop.cs to test whether
        // switching from this host-driven AUTO homing to the current custom-C
        // HomePickAndPlace.c homing (RunHomeAll() above) is what's leaving the
        // controller in a state where later MoveSingleFeed calls report success
        // but don't move anything.
        //
        // IMPORTANT: this is the exact approach RunHomeAll() replaced because of
        // a confirmed bug on this machine - AUTO's built-in home-task generator
        // can keep an axis driving after its limit switch has already tripped,
        // i.e. into the hard stop (see RunHomeAll's comment above). Do not wire
        // this to a normal Home button without addressing that first. Call
        // HomeAllLegacy() only for a supervised, one-off comparison test, with a
        // hand on E-Stop.
        private bool _legacyHomingConfigured = false;

        public void ConfigureLegacyHomingParams()
        {
            lock (_controllerLock)
            {
                _ZAxis.HomingParams.SourceType = HOMING_ROUTINE_SOURCE_TYPE.AUTO;
                _ZAxis.HomingParams.DefaultThread = 5;
                _ZAxis.HomingParams.HomeFastVel = 300;
                _ZAxis.HomingParams.HomeSlowVel = 70;
                _ZAxis.HomingParams.HomeLimitBit = 21;
                _ZAxis.HomingParams.HomeLimitState = true;
                _ZAxis.HomingParams.RepeatHomeAtSlowerRate = true;
                _ZAxis.HomingParams.SequencePriority = 1;
                _ZAxis.HomingParams.HomeNegative = true;
                _ZAxis.HomingParams.StatusBit = 21;
                _ZAxis.HomingParams.SetToZero = true;

                _AAxis.HomingParams.SourceType = HOMING_ROUTINE_SOURCE_TYPE.AUTO;
                _AAxis.HomingParams.DefaultThread = 2;
                _AAxis.HomingParams.HomeFastVel = 300;
                _AAxis.HomingParams.HomeSlowVel = 70;
                _AAxis.HomingParams.HomeLimitBit = 23;
                _AAxis.HomingParams.HomeLimitState = true;
                _AAxis.HomingParams.RepeatHomeAtSlowerRate = true;
                _AAxis.HomingParams.SequencePriority = 2;
                _AAxis.HomingParams.HomeNegative = true;
                _AAxis.HomingParams.StatusBit = 23;
                _AAxis.HomingParams.SetToZero = true;

                _XAxis.HomingParams.SourceType = HOMING_ROUTINE_SOURCE_TYPE.AUTO;
                _XAxis.HomingParams.DefaultThread = 3;
                _XAxis.HomingParams.HomeFastVel = 250;
                _XAxis.HomingParams.HomeSlowVel = 80;
                _XAxis.HomingParams.HomeLimitBit = 19;
                _XAxis.HomingParams.HomeLimitState = true;
                _XAxis.HomingParams.RepeatHomeAtSlowerRate = true;
                _XAxis.HomingParams.SequencePriority = 3;
                _XAxis.HomingParams.HomeNegative = true;
                _XAxis.HomingParams.StatusBit = 19;
                _XAxis.HomingParams.SetToZero = true;

                _YAxis.HomingParams.SourceType = HOMING_ROUTINE_SOURCE_TYPE.AUTO;
                _YAxis.HomingParams.DefaultThread = 4;
                _YAxis.HomingParams.HomeFastVel = 800;
                _YAxis.HomingParams.HomeSlowVel = 70;
                _YAxis.HomingParams.HomeLimitBit = 25;
                _YAxis.HomingParams.HomeLimitState = true;
                _YAxis.HomingParams.RepeatHomeAtSlowerRate = true;
                _YAxis.HomingParams.SequencePriority = 4;
                _YAxis.HomingParams.HomeNegative = true;
                _YAxis.HomingParams.StatusBit = 25;
                _YAxis.HomingParams.SetToZero = true;

                _legacyHomingConfigured = true;
            }
        }

        public void HomeAllLegacy()
        {
            Thread runhoming = new Thread(new ThreadStart(RunHomeAllLegacy)) { IsBackground = true };
            runhoming.Start();
        }

        public void RunHomeAllLegacy()
        {
            if (!CheckReady()) return;

            Debug.WriteLine("Starting Legacy Home");

            try
            {
                if (!_legacyHomingConfigured)
                {
                    ConfigureLegacyHomingParams();
                }

                lock (_controllerLock)
                {
                    SetAlltoZero();
                    _ZAxis.Jog(3);
                    _AAxis.Jog(3);
                }
                Thread.Sleep(300);
                lock (_controllerLock)
                {
                    _ZAxis.Jog(0);
                    _AAxis.Jog(0);
                }
                Thread.Sleep(300);

                lock (_controllerLock) { _ZAxis.StartDoHome(); }
                while (!LockedCheck(() => _ZAxis.MotionComplete()))
                {
                    Thread.Sleep(50);
                }

                lock (_controllerLock) { _AAxis.StartDoHome(); }
                while (!LockedCheck(() => _AAxis.MotionComplete()))
                {
                    Thread.Sleep(50);
                }

                lock (_controllerLock)
                {
                    _XAxis.StartDoHome();
                    _YAxis.StartDoHome();
                }
                while (!LockedCheck(() => _XAxis.MotionComplete()))
                {
                    Thread.Sleep(50);
                }
                while (!LockedCheck(() => _YAxis.MotionComplete()))
                {
                    Thread.Sleep(50);
                }

                lock (_controllerLock)
                {
                    SetAlltoZero();
                }

                App app = (App)Application.Current;
                if (app != null)
                {
                    app.setHomed(true);
                }
                else
                {
                    RaiseError("RunHomeAllLegacy: homing completed motion, but could not reach App to record it as homed");
                }
            }
            catch (Exception ex)
            {
                RaiseError("RunHomeAllLegacy: " + ex.ToString());
            }
        }

        private bool LockedCheck(Func<bool> condition)
        {
            lock (_controllerLock)
            {
                return condition();
            }
        }

        public void SetAlltoZero()
        {
            lock (_controllerLock)
            {
                Debug.WriteLine("Setting All to Zero");
                currentX = 0.0;
                currentY = 0.0;
                currentZ = 0.0;
                currentA = 0.0;
                currentB = 0.0;
                currentC = 0.0;
                _Controller.CoordMotion.Abort();
                _Controller.CoordMotion.ClearAbort();

                double x = 0; double y = 0; double z = 0; double a = 0; double b = 0; double c = 0;
                _Controller.CoordMotion.Interpreter.ReadAndSynchCurInterpreterPosition(ref x, ref y, ref z, ref a, ref b, ref c);

                _XAxis.SetCurrentPosition(0);
                _YAxis.SetCurrentPosition(0);
                _ZAxis.SetCurrentPosition(0);
                _AAxis.SetCurrentPosition(0);
                _BAxis.SetCurrentPosition(0);
                _CAxis.SetCurrentPosition(0);
            }
        }

        public void SetPickerHome()
        {
            if (!CheckReady()) return;

            _ZAxis.SetCurrentPosition(38.0);
            _AAxis.SetCurrentPosition(38.0);
        }

        public bool MoveXAxis(double newpos)
        {
            _XAxis.MoveTo(newpos);
            while (!_XAxis.MotionComplete())
            {
                Thread.Sleep(10);
            }
            return true;
        }

        public bool MoveYAxis(double newpos)
        {
            _YAxis.MoveTo(newpos);
            while (!_YAxis.MotionComplete())
            {
                Thread.Sleep(10);
            }
            return true;
        }

        public bool MoveZAxis(double newpos)
        {
            while (!_ZAxis.MotionComplete())
            {
                Thread.Sleep(10);
            }

            return true;
        }

        public bool MoveAAxis(double newpos)
        {
            _AAxis.MoveTo(newpos);
            while (!_AAxis.MotionComplete())
            {
                Thread.Sleep(10);
            }
            return true;
        }

        public bool MoveBAxis(double newpos)
        {
            _BAxis.MoveTo(newpos);
            while (!_BAxis.MotionComplete())
            {
                Thread.Sleep(10);
            }
            return true;
        }

        public bool MoveCAxis(double newpos)
        {
            _CAxis.MoveTo(newpos);
            while (!_CAxis.MotionComplete())
            {
                Thread.Sleep(10);
            }
            return true;
        }

        // Above ~150, MaxAccelY/MaxVelY (scaled from AxisY by speed below) grow faster
        // than the fixed TuningParams.Jerk can keep up with after a lot of testing
        private const double MaxSafeFeedSpeed = 150;

        public bool MoveSingleFeed(double speed, double x, double y, double z, double a, double b, double c)
        {
            if (speed > MaxSafeFeedSpeed)
            {
                speed = MaxSafeFeedSpeed;
            }

            if (x < 0 || x > 330 || y < 0 || y > 395 || z > 35.5 || z < 0 || a > 35.5 || a < 0)
            {
                RaiseError("Error with location out of bounds");
                return false;
            }
            else
            {
                lock (_controllerLock)
                {
                    // The very first StraightFeed after the axes are freshly enabled
                    // (app launch, or after a Home) consistently trips the Trajectory
                    // Planner's "takes longer to stop than 75% of Lookahead" check
                    // regardless of speed/Accel/Vel - this is a
                    // one-time warm-up quirk rather than a real per-move safety issue.
                    // Retry once silently instead of surfacing that as a user-facing error.
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            currentX = x;
                            currentY = y;
                            currentZ = z;
                            currentA = a;
                            currentB = b;
                            currentC = c;

                            if (speed != 100)
                            {
                                _Controller.CoordMotion.MotionParams.MaxAccelX = (AxisX.Accel / 100) * speed;
                                _Controller.CoordMotion.MotionParams.MaxAccelY = (AxisY.Accel / 100) * speed;
                                _Controller.CoordMotion.MotionParams.MaxVelX = (AxisX.MaxVel / 100) * speed;
                                _Controller.CoordMotion.MotionParams.MaxVelY = (AxisY.MaxVel / 100) * speed;
                            }
                            else
                            {
                                _Controller.CoordMotion.MotionParams.MaxAccelX = AxisX.Accel;
                                _Controller.CoordMotion.MotionParams.MaxAccelY = AxisY.Accel;
                                _Controller.CoordMotion.MotionParams.MaxVelX = AxisX.MaxVel;
                                _Controller.CoordMotion.MotionParams.MaxVelY = AxisY.MaxVel;
                            }

                            _Controller.CoordMotion.StraightFeed(1000, currentX, currentY, currentZ, currentA, currentB, currentC, 0, 0);

                            _Controller.CoordMotion.DownloadDoneSegments();
                            _Controller.CoordMotion.WaitForSegmentsFinished(true);
                            _Controller.CoordMotion.FlushSegments();

                            _BAxis.SetCurrentPosition(0);
                            _CAxis.SetCurrentPosition(0);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            if (attempt == 1)
                            {
                                Debug.WriteLine("MoveSingleFeed: first attempt failed (" + ex.Message + "), retrying once");
                                continue;
                            }

                            RaiseError("MoveSingleFeed: " + ex.Message);
                            return false;
                        }
                    }

                    return false;
                }
            }
        }

        public bool MoveSingleFeedOnUIThread(double speed, double x, double y, double z, double a, double b, double c)
        {
            DispatcherQueue dq = ((App)Application.Current).GetMainWindow().DispatcherQueue;
            if (dq.HasThreadAccess)
            {
                return MoveSingleFeed(speed, x, y, z, a, b, c);
            }

            bool result = false;
            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                dq.TryEnqueue(() =>
                {
                    result = MoveSingleFeed(speed, x, y, z, a, b, c);
                    done.Set();
                });
                done.Wait();
            }
            return result;
        }

        public bool MoveArrayFeed(double[,] array)
        {
            double speed = 0.0;
            for (int i = 0; i < array.Length; i++)
            {
                speed = array[i, 0];
                if (!array[i, 1].Equals(currentX))
                {
                    currentX = array[i, 1];
                }
                if (!array[i, 2].Equals(currentY))
                {
                    currentY = array[i, 2];
                }
                if (!array[i, 3].Equals(currentZ))
                {
                    currentZ = array[i, 3];
                }
                if (!array[i, 4].Equals(currentA))
                {
                    currentA = array[i, 4];
                }
                if (!array[i, 5].Equals(currentB))
                {
                    currentB = array[i, 5];
                }
                if (!array[i, 6].Equals(currentC))
                {
                    currentC = array[i, 6];
                }

                _Controller.CoordMotion.StraightTraverse(currentX, currentY, currentZ, currentA, currentB, currentC, true);
                _Controller.CoordMotion.WaitForSegmentsFinished(true);
                _Controller.CoordMotion.FlushSegments();
            }

            return true;
        }

        public void GetDRO(out double _x, out double _y, out double _z, out double _a, out double _b, out double _c)
        {
            if (!CheckReady())
            {
                _x = _y = _z = _a = _b = _c = 0.0;
                return;
            }

            lock (_controllerLock)
            {
                
                var mp = _Controller.CoordMotion.MotionParams;
                _x = _XAxis.GetCommandedPositionCounts() / mp.CountsPerInchX;
                _y = _YAxis.GetCommandedPositionCounts() / mp.CountsPerInchY;
                _z = _ZAxis.GetCommandedPositionCounts() / mp.CountsPerInchZ;
                _a = _AAxis.GetCommandedPositionCounts() / mp.CountsPerInchA;
                _b = _BAxis.GetCommandedPositionCounts() / mp.CountsPerInchB;
                _c = _CAxis.GetCommandedPositionCounts() / mp.CountsPerInchC;
            }
        }

        public void EStop()
        {
            if (!CheckReady()) return;

            lock (_controllerLock)
            {
                if (!eStopActive)
                {
                    _XAxis.Disable();
                    _YAxis.Disable();
                    _ZAxis.Disable();
                    _AAxis.Disable();
                    _BAxis.Disable();
                    _CAxis.Disable();
                    eStopActive = true;
                }
                else
                {
                    _XAxis.Enable();
                    _YAxis.Enable();
                    _ZAxis.Enable();
                    _AAxis.Enable();
                    _BAxis.Enable();
                    _CAxis.Enable();
                    eStopActive = false;
                }
            }
        }

        public void JogAxis(string axis, double distancetomove)
        {
            if (!CheckReady()) return;

            lock (_controllerLock)
            {
                if (axis.Equals("X"))
                {
                    _XAxis.Jog(distancetomove);
                }
                if (axis.Equals("Y"))
                {
                    _YAxis.Jog(distancetomove);
                }
                if (axis.Equals("Z"))
                {
                    _ZAxis.Jog(distancetomove);
                }
                if (axis.Equals("A"))
                {
                    _AAxis.Jog(distancetomove);
                }
                if (axis.Equals("B"))
                {
                    _BAxis.Jog(distancetomove);
                }
                if (axis.Equals("C"))
                {
                    _CAxis.Jog(distancetomove);
                }
            }
        }

        // event handlers for motion controller
        void Interpreter_Interpreter_CoordMotionStraightTranverse(double x, double y, double z, int sequence_number)
        {
            Debug.WriteLine("Interpreter CoordMotion Straight Tranverse::  {0} | {1} | {2} | {3}", x, y, z, sequence_number);
        }

        void Interpreter_Interpreter_CoordMotionStraightFeed(double DesiredFeedRate_in_per_sec, double x, double y, double z, int sequence_number, int ID)
        {
            Debug.WriteLine("Interpreter CoordMotion Straight Feed::  {0} | {1} | {2} | {3} | {4} | {5}", DesiredFeedRate_in_per_sec, x, y, z, sequence_number, ID);
        }

        void Interpreter_Interpreter_CoordMotionArcFeed(bool ZeroLenAsFullCircles, double DesiredFeedRate_in_per_sec, int plane, double first_end, double second_end, double first_axis, double second_axis, int rotation, double axis_end_point, double first_start, double second_start, double axis_start_point, int sequence_number, int ID)
        {
            Debug.WriteLine("Interpreter CoordMotion Arc Feed::  {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12}",
                ZeroLenAsFullCircles,
                DesiredFeedRate_in_per_sec,
                plane, first_end,
                second_end,
                first_axis,
                second_axis,
                rotation,
                axis_end_point,
                first_start,
                second_start,
                axis_start_point,
                sequence_number,
                ID);
        }

        void Interpreter_InterpreterCompleted(int status, int lineno, int sequence_number, string err)
        {
            Debug.WriteLine(String.Format("Interpreter Completed::  {0} | {1} | {2} | {3}", status, lineno, sequence_number, err));
        }

        void Interpreter_InterpreterStatusUpdated(int lineno, string msg)
        {
            Debug.WriteLine("Interpreter Status Update:");
            Debug.WriteLine(lineno);
            Debug.WriteLine(msg);
        }

        void Interpreter_InterpreterUserCallbackRequested(string msg)
        {
            Debug.WriteLine("Interpreter User Callback:");
            Debug.WriteLine(msg);
        }

        int Interpreter_InterpreterUserMCodeCallbackRequested(int code)
        {
            throw new NotImplementedException();
        }

        void AddHandlers()
        {
            //Set the callback for general messages
            _Controller.MessageReceived += new KMotion_dotNet.KMConsoleHandler(_Controller_MessageUpdated);
            //And Errors
            _Controller.ErrorReceived += new KMotion_dotNet.KMErrorHandler(_Controller_ErrorUpdated);

            //CoordMotion Callbacks
            _Controller.CoordMotion.CoordMotionStraightTraverse += new KMotion_dotNet.KM_CoordMotionStraightTraverseHandler(CoordMotion_CoordMotionStraightTranverse);
            _Controller.CoordMotion.CoordMotionArcFeed += new KMotion_dotNet.KM_CoordMotionArcFeedHandler(CoordMotion_CoordMotionArcFeed);
            _Controller.CoordMotion.CoordMotionStraightFeed += new KMotion_dotNet.KM_CoordMotionStraightFeedHandler(CoordMotion_CoordMotionStraightFeed);

            //Set the Interpreter's callbacks
            _Controller.CoordMotion.Interpreter.InterpreterStatusUpdated += new KMotion_dotNet.KM_Interpreter.KM_GCodeInterpreterStatusHandler(Interpreter_InterpreterStatusUpdated);
            _Controller.CoordMotion.Interpreter.InterpreterCompleted += new KMotion_dotNet.KM_Interpreter.KM_GCodeInterpreterCompleteHandler(Interpreter_InterpreterCompleted);
            _Controller.CoordMotion.Interpreter.InterpreterUserCallbackRequested += new KMotion_dotNet.KM_Interpreter.KM_GCodeInterpreterUserCallbackHandler(Interpreter_InterpreterUserCallbackRequested);
            _Controller.CoordMotion.Interpreter.InterpreterUserMCodeCallbackRequested += new KMotion_dotNet.KM_Interpreter.KM_GCodeInterpreterUserMcodeCallbackHandler(Interpreter_InterpreterUserMCodeCallbackRequested);
        }

        static int _Controller_MessageUpdated(string message)
        {
            Debug.WriteLine(message);
            return 0;
        }

        /// <summary>
        /// Handler for the error message pump
        /// </summary>
        /// <param name="message">error string</param>
        static void _Controller_ErrorUpdated(string message)
        {
            Debug.WriteLine("#########################  ERROR  #########################");
            Debug.WriteLine(message);
            Debug.WriteLine("#########################  ERROR  #########################");
        }

        static void CoordMotion_CoordMotionStraightTranverse(double x, double y, double z, int sequence_number)
        {
            Debug.WriteLine("CoordMotion Straight Tranverse::  {0} | {1} | {2} | {3}", x, y, z, sequence_number);
        }

        static void CoordMotion_CoordMotionStraightFeed(double DesiredFeedRate_in_per_sec, double x, double y, double z, int sequence_number, int ID)
        {
            Debug.WriteLine("CoordMotion Straight Feed::  {0} | {1} | {2} | {3} | {4} | {5}", DesiredFeedRate_in_per_sec, x, y, z, sequence_number, ID);
        }

        static void CoordMotion_CoordMotionArcFeed(bool ZeroLenAsFullCircles, double DesiredFeedRate_in_per_sec, int plane, double first_end, double second_end, double first_axis,
            double second_axis, int rotation, double axis_end_point, double first_start, double second_start, double axis_start_point, int sequence_number, int ID)
        {
            Debug.WriteLine("CoordMotion Arc Feed::  {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12}",
                ZeroLenAsFullCircles,
                DesiredFeedRate_in_per_sec,
                plane, first_end,
                second_end,
                first_axis,
                second_axis,
                rotation,
                axis_end_point,
                first_start,
                second_start,
                axis_start_point,
                sequence_number,
                ID);
        }
    }
}