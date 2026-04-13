using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CapstoneController.Interop;
using CapstoneController.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Threading;
using CapstoneController.Views;

namespace CapstoneController.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private Task<int>? _nativeRunTask;
        private CancellationTokenSource? _volumeCts;
        private bool _stopRequested;
        private Window? _graphDetailWindow;

        private readonly DispatcherTimer _accelTimer;
        private readonly object _accelMu = new();
        private readonly List<double> _accelY = new(capacity: 400);
        private uint _lastAccelTimestampUs;
        private bool _hasLastAccelTimestamp;

        public MainWindowViewModel()
        {
            ControlModeIndex = 0;
            IsManualMode = true;
            FrequencyInput = "1000";
            SweepStartFrequency = "100";
            SweepEndFrequency = "1000";
            SweepRate = "100";
            ConnectionStatus = "CONNECTED";
            CurrentFrequencyDisplay = "0 Hz";
            OutputStatus = "STOPPED";
            StatusText = "System idle";

            SetFrequencyCommand = new AsyncRelayCommand(SetFrequencyAsync);
            StartSweepCommand = new AsyncRelayCommand(StartSweepAsync);
            StartOutputCommand = new AsyncRelayCommand(StartOutputAsync);
            StopCommand = new RelayCommand(StopOutput);
            ShowOutputCommand = new RelayCommand(TogglePreview);
            QuitCommand = new RelayCommand(Quit);

            ZoomInCommand = new RelayCommand(ZoomIn);
            ZoomOutCommand = new RelayCommand(ZoomOut);

            FitAccelSineCommand = new RelayCommand(FitAccelSine);
            ClearAccelCommand = new RelayCommand(ClearAccel);

            _accelTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50),
            };
            _accelTimer.Tick += (_, _) => PollAccel();
            _accelTimer.Start();

            OpenFrequencyNumpadCommand = new RelayCommand(OpenFrequencyNumpad);
            CloseFrequencyNumpadCommand = new RelayCommand(CloseFrequencyNumpad);
            FrequencyNumpadKeyCommand = new RelayCommand<string>(FrequencyNumpadKey);
            FrequencyNumpadBackspaceCommand = new RelayCommand(FrequencyNumpadBackspace);
            FrequencyNumpadClearCommand = new RelayCommand(FrequencyNumpadClear);
            FrequencyNumpadOkCommand = new RelayCommand(FrequencyNumpadOk);
        }

        [ObservableProperty]
        private string frequencyInput = string.Empty;

        [ObservableProperty]
        private int controlModeIndex;

        [ObservableProperty]
        private bool isManualMode;

        public double ManualFrequencyValue
        {
            get
            {
                if (TryParseHz(FrequencyInput, out var hz))
                    return hz;

                return 0;
            }
            set
            {
                var clamped = value;
                if (double.IsNaN(clamped) || double.IsInfinity(clamped))
                    clamped = 0;
                if (clamped < 0)
                    clamped = 0;

                FrequencyInput = clamped.ToString("0.###", CultureInfo.InvariantCulture);
            }
        }

        [ObservableProperty]
        private double setFrequencyHz;

        [ObservableProperty]
        private string sweepStartFrequency = string.Empty;

        [ObservableProperty]
        private string sweepEndFrequency = string.Empty;

        [ObservableProperty]
        private string sweepRate = string.Empty;

        [ObservableProperty]
        private string connectionStatus = "DISCONNECTED";

        [ObservableProperty]
        private string currentFrequencyDisplay = "0 Hz";

        [ObservableProperty]
        private string outputStatus = "STOPPED";

        [ObservableProperty]
        private string statusText = "System idle";

        [ObservableProperty]
        private bool isShowingOutput = true;

        [ObservableProperty]
        private double graphZoom = 1.0;

        [ObservableProperty]
        private string accelStatus = "No accel data";

        [ObservableProperty]
        private string accelLastSampleText = "";

        [ObservableProperty]
        private string accelSineFitEquation = "Fit: (not calculated)";

        [ObservableProperty]
        private double[]? accelYSamples;

        [ObservableProperty]
        private double[]? accelSineFitSamples;

        [ObservableProperty]
        private bool isAccelFitOverlayEnabled = true;

        [ObservableProperty]
        private double volumePercent = 50;

        [ObservableProperty]
        private bool isFrequencyNumpadOpen;

        public IAsyncRelayCommand SetFrequencyCommand { get; }
        public IAsyncRelayCommand StartSweepCommand { get; }
        public IAsyncRelayCommand StartOutputCommand { get; }
        public IRelayCommand StopCommand { get; }
        public IRelayCommand ShowOutputCommand { get; }
        public IRelayCommand QuitCommand { get; }

        public IRelayCommand ZoomInCommand { get; }
        public IRelayCommand ZoomOutCommand { get; }

        public IRelayCommand FitAccelSineCommand { get; }
        public IRelayCommand ClearAccelCommand { get; }

        public IRelayCommand OpenFrequencyNumpadCommand { get; }
        public IRelayCommand CloseFrequencyNumpadCommand { get; }
        public IRelayCommand<string> FrequencyNumpadKeyCommand { get; }
        public IRelayCommand FrequencyNumpadBackspaceCommand { get; }
        public IRelayCommand FrequencyNumpadClearCommand { get; }
        public IRelayCommand FrequencyNumpadOkCommand { get; }

        private void Quit()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
                return;
            }

            // Fallback: best-effort shutdown for other lifetimes.
            Environment.Exit(0);
        }

        private async Task SetFrequencyAsync()
        {
            if (!TryParseHz(FrequencyInput, out var hz))
            {
                StatusText = "Invalid manual frequency";
                return;
            }

            if (hz <= 0)
            {
                StatusText = "Frequency must be greater than 0 Hz";
                return;
            }

            SetFrequencyHz = hz;
            CurrentFrequencyDisplay = $"{hz:0.###} Hz";

            // If the native loop is already running, update frequency in-place.
            if (_nativeRunTask is { IsCompleted: false })
            {
                var rc = NativeMethods.SetFrequencyHz(hz);
                StatusText = rc == 0
                    ? $"Frequency updated to {hz:0.###} Hz"
                    : "Failed to update frequency";
                return;
            }

            StatusText = $"Manual frequency set to {hz:0.###} Hz";

            await RunNativeAsync(
                () => NativeMethods.StartTone(hz),
                startMessage: $"Starting native output at {hz:0.###} Hz…",
                runningStatus: "RUNNING");
        }

        private async Task StartOutputAsync()
        {
            if (SetFrequencyHz <= 0)
            {
                if (!TryParseHz(FrequencyInput, out var hz) || hz <= 0)
                {
                    OutputStatus = "STOPPED";
                    StatusText = "Invalid manual frequency";
                    return;
                }

                SetFrequencyHz = hz;
            }

            OutputStatus = "RUNNING";
            CurrentFrequencyDisplay = $"{SetFrequencyHz:0.###} Hz";
            StatusText = $"Output running at {SetFrequencyHz:0.###} Hz";

            await RunNativeAsync(
                () => NativeMethods.StartTone(SetFrequencyHz),
                startMessage: $"Starting native output at {SetFrequencyHz:0.###} Hz…",
                runningStatus: "RUNNING");
        }

        private async Task StartSweepAsync()
        {
            if (!TryParseHz(SweepStartFrequency, out var startHz))
            {
                StatusText = "Invalid sweep start frequency";
                return;
            }

            if (!TryParseHz(SweepEndFrequency, out var endHz))
            {
                StatusText = "Invalid sweep end frequency";
                return;
            }

            if (!TryParseHz(SweepRate, out var rateHz))
            {
                StatusText = "Invalid sweep rate";
                return;
            }

            if (startHz <= 0 || endHz <= 0)
            {
                StatusText = "Sweep frequencies must be greater than 0 Hz";
                return;
            }

            if (endHz <= startHz)
            {
                StatusText = "End frequency must be greater than start frequency";
                return;
            }

            if (rateHz <= 0)
            {
                StatusText = "Sweep rate must be greater than 0";
                return;
            }

            SetFrequencyHz = startHz;
            CurrentFrequencyDisplay = $"{startHz:0.###} Hz";
            OutputStatus = "SWEEPING";
            StatusText = $"Sweep started: {startHz:0.###} Hz to {endHz:0.###} Hz at {rateHz:0.###}";

            await RunNativeAsync(
                () => NativeMethods.StartSweep(startHz, endHz, rateHz),
                startMessage: $"Starting native sweep {startHz:0.###}→{endHz:0.###} (step/rate {rateHz:0.###})…",
                runningStatus: "SWEEPING");
        }

        private void StopOutput()
        {
            OutputStatus = "STOPPED";

            if (_nativeRunTask is { IsCompleted: false })
            {
                _stopRequested = true;
                CurrentFrequencyDisplay = "0 Hz";
                var rc = NativeMethods.Stop();
                StatusText = rc == 0 ? "Stopping output…" : "Output already stopped";
                return;
            }

            // Best-effort stop even if not running.
            NativeMethods.Stop();
            CurrentFrequencyDisplay = "0 Hz";
            StatusText = "Output stopped";
        }

        private void TogglePreview()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_graphDetailWindow is { IsVisible: true })
                {
                    _graphDetailWindow.Activate();
                    return;
                }

                var window = new GraphDetailWindow
                {
                    DataContext = this,
                };

                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_graphDetailWindow, window))
                        _graphDetailWindow = null;
                };

                _graphDetailWindow = window;

                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    window.Show(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }
            });

            StatusText = "Opened detailed graph";
        }

        private void ZoomIn()
        {
            GraphZoom = Math.Clamp(GraphZoom * 1.25, 0.25, 8.0);
        }

        private void ZoomOut()
        {
            GraphZoom = Math.Clamp(GraphZoom / 1.25, 0.25, 8.0);
        }

        private void PollAccel()
        {
            var rc = NativeMethods.TryGetAccelSample(out var s);
            if (rc == 1)
            {
                AccelStatus = "No accel data (start output to begin sampling)";
                return;
            }
            if (rc != 0)
            {
                AccelStatus = "Accel read error";
                return;
            }

            // De-dupe identical timestamps when timestamped API is available.
            if (s.TimestampUs != 0)
            {
                if (_hasLastAccelTimestamp && s.TimestampUs == _lastAccelTimestampUs)
                    return;
                _hasLastAccelTimestamp = true;
                _lastAccelTimestampUs = s.TimestampUs;
            }

            AccelStatus = "Receiving accel data";
            AccelLastSampleText = $"t={s.TimestampUs}us  x={s.X}  y={s.Y}  z={s.Z}  temp={(s.TempCentiC / 100.0):0.00}C";

            lock (_accelMu)
            {
                _accelY.Add(s.Y);
                const int max = 400;
                if (_accelY.Count > max)
                    _accelY.RemoveRange(0, _accelY.Count - max);

                // Update plotted samples (copy for thread-safety + render invalidation).
                AccelYSamples = _accelY.ToArray();
            }
        }

        private void ClearAccel()
        {
            lock (_accelMu)
            {
                _accelY.Clear();
            }

            AccelYSamples = Array.Empty<double>();
            AccelSineFitSamples = null;
            AccelSineFitEquation = "Fit: (not calculated)";
            AccelStatus = "Cleared accel buffer";
        }

        private void FitAccelSine()
        {
            double[] y;
            lock (_accelMu)
            {
                if (_accelY.Count < 60)
                {
                    AccelSineFitEquation = "Fit: not enough samples yet";
                    return;
                }

                // Fit last ~1 second (up to 200 samples at 200 Hz).
                const int fitN = 200;
                var start = Math.Max(0, _accelY.Count - fitN);
                y = _accelY.GetRange(start, _accelY.Count - start).ToArray();
            }

            // If timestamped API isn't available, assume nominal accel sample rate.
            const double assumedHz = 200.0;
            var dt = 1.0 / assumedHz;

            var best = FindBestSineFit(y, dt);
            if (!best.Success)
            {
                AccelSineFitEquation = "Fit: failed";
                return;
            }

            AccelSineFitSamples = best.Fit;
            AccelSineFitEquation = $"y(t) = {best.Offset:0.###} + {best.Amplitude:0.###} * sin(2π * {best.FrequencyHz:0.###} * t + {best.PhaseRad:0.###})   (R²={best.R2:0.###})";
            AccelStatus = "Sine fit updated";
        }

        private readonly record struct SineFitResult(bool Success, double FrequencyHz, double Amplitude, double PhaseRad, double Offset, double R2, double[] Fit);

        private static SineFitResult FindBestSineFit(double[] y, double dt)
        {
            // Guard
            if (y.Length < 3 || !(dt > 0))
                return new SineFitResult(false, 0, 0, 0, 0, 0, Array.Empty<double>());

            // Remove a constant baseline in the model itself (offset term), but we still compute SST for R^2.
            var mean = 0.0;
            for (var i = 0; i < y.Length; i++) mean += y[i];
            mean /= y.Length;

            var sst = 0.0;
            for (var i = 0; i < y.Length; i++)
            {
                var d = y[i] - mean;
                sst += d * d;
            }
            if (!(sst > 1e-9))
                sst = 1.0;

            // Sampling ~200 Hz => Nyquist ~100 Hz. Keep a margin.
            var fMin = 0.5;
            var fMax = 90.0;

            // Coarse then refine around best.
            var bestF = 0.0;
            var bestErr = double.PositiveInfinity;
            var bestABC = (a: 0.0, b: 0.0, c: mean);

            void Evaluate(double f)
            {
                var w = 2.0 * Math.PI * f;
                // Solve least squares for y = a*sin(wt) + b*cos(wt) + c
                // Normal equations for 3 params.
                double s11 = 0, s12 = 0, s13 = 0, s22 = 0, s23 = 0, s33 = y.Length;
                double t1 = 0, t2 = 0, t3 = 0;

                for (var i = 0; i < y.Length; i++)
                {
                    var t = i * dt;
                    var s = Math.Sin(w * t);
                    var c0 = Math.Cos(w * t);
                    var yi = y[i];

                    s11 += s * s;
                    s12 += s * c0;
                    s13 += s;
                    s22 += c0 * c0;
                    s23 += c0;

                    t1 += s * yi;
                    t2 += c0 * yi;
                    t3 += yi;
                }

                // Matrix:
                // [s11 s12 s13] [a] = [t1]
                // [s12 s22 s23] [b]   [t2]
                // [s13 s23 s33] [c]   [t3]
                // Solve via Cramer's rule / elimination (small 3x3).

                // Gaussian elimination
                var A11 = s11; var A12 = s12; var A13 = s13; var B1 = t1;
                var A21 = s12; var A22 = s22; var A23 = s23; var B2 = t2;
                var A31 = s13; var A32 = s23; var A33 = s33; var B3 = t3;

                // Pivot 1
                if (Math.Abs(A11) < 1e-12)
                    return;
                var m21 = A21 / A11;
                var m31 = A31 / A11;
                A21 -= m21 * A11; A22 -= m21 * A12; A23 -= m21 * A13; B2 -= m21 * B1;
                A31 -= m31 * A11; A32 -= m31 * A12; A33 -= m31 * A13; B3 -= m31 * B1;

                // Pivot 2
                if (Math.Abs(A22) < 1e-12)
                    return;
                var m32 = A32 / A22;
                A31 -= m32 * A21; A32 -= m32 * A22; A33 -= m32 * A23; B3 -= m32 * B2;

                // Back-sub
                if (Math.Abs(A33) < 1e-12)
                    return;

                var c = B3 / A33;
                var b = (B2 - A23 * c) / A22;
                var a = (B1 - A12 * b - A13 * c) / A11;

                // Compute SSE
                var sse = 0.0;
                for (var i = 0; i < y.Length; i++)
                {
                    var t = i * dt;
                    var yhat = (a * Math.Sin(w * t)) + (b * Math.Cos(w * t)) + c;
                    var e = y[i] - yhat;
                    sse += e * e;
                }

                if (sse < bestErr)
                {
                    bestErr = sse;
                    bestF = f;
                    bestABC = (a, b, c);
                }
            }

            for (double f = fMin; f <= fMax; f += 0.5)
                Evaluate(f);

            if (!(bestF > 0))
                return new SineFitResult(false, 0, 0, 0, 0, 0, Array.Empty<double>());

            var refineMin = Math.Max(fMin, bestF - 1.0);
            var refineMax = Math.Min(fMax, bestF + 1.0);
            for (double f = refineMin; f <= refineMax; f += 0.05)
                Evaluate(f);

            var wBest = 2.0 * Math.PI * bestF;
            var (aa, bb, cc) = bestABC;
            var amp = Math.Sqrt((aa * aa) + (bb * bb));
            var phase = Math.Atan2(bb, aa);

            var fit = new double[y.Length];
            for (var i = 0; i < y.Length; i++)
            {
                var t = i * dt;
                fit[i] = (aa * Math.Sin(wBest * t)) + (bb * Math.Cos(wBest * t)) + cc;
            }

            var r2 = 1.0 - (bestErr / sst);
            return new SineFitResult(true, bestF, amp, phase, cc, r2, fit);
        }

        private void OpenFrequencyNumpad()
        {
            IsFrequencyNumpadOpen = true;
        }

        private void CloseFrequencyNumpad()
        {
            IsFrequencyNumpadOpen = false;
        }

        private void FrequencyNumpadOk()
        {
            IsFrequencyNumpadOpen = false;
        }

        private void FrequencyNumpadClear()
        {
            FrequencyInput = string.Empty;
        }

        private void FrequencyNumpadBackspace()
        {
            if (string.IsNullOrEmpty(FrequencyInput))
                return;

            FrequencyInput = FrequencyInput[..^1];
        }

        private void FrequencyNumpadKey(string? key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (key == ".")
            {
                if (string.IsNullOrEmpty(FrequencyInput))
                {
                    FrequencyInput = "0.";
                    return;
                }

                if (FrequencyInput.Contains('.'))
                    return;

                FrequencyInput += ".";
                return;
            }

            if (key.Length == 1 && char.IsDigit(key[0]))
            {
                if (FrequencyInput == "0")
                    FrequencyInput = key;
                else
                    FrequencyInput += key;
            }
        }

        private static bool TryParseHz(string? text, out double hz)
        {
            hz = 0;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Trim();

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out hz) && hz >= 0)
                return true;

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out hz) && hz >= 0)
                return true;

            hz = 0;
            return false;
        }

        partial void OnFrequencyInputChanged(string value)
        {
            OnPropertyChanged(nameof(ManualFrequencyValue));
        }

        partial void OnControlModeIndexChanged(int value)
        {
            IsManualMode = value == 0;
        }

        partial void OnVolumePercentChanged(double value)
        {
            // Debounce to avoid spamming pactl/amixer while dragging.
            _volumeCts?.Cancel();
            _volumeCts?.Dispose();
            _volumeCts = new CancellationTokenSource();

            var token = _volumeCts.Token;
            var percent = (int)Math.Round(Math.Clamp(value, 0, 100));

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(120, token).ConfigureAwait(false);
                    var ok = await SystemVolumeService.TrySetVolumePercentAsync(percent, token).ConfigureAwait(false);

                    if (!ok)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            // Keep this subtle; don't overwrite more important status.
                            if (StatusText == "System idle")
                                StatusText = "Volume control unavailable";
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
            }, token);
        }

        private async Task RunNativeAsync(Func<int> nativeCall, string startMessage, string runningStatus)
        {
            if (_nativeRunTask is { IsCompleted: false })
            {
                StatusText = "Native output already running";
                return;
            }

            OutputStatus = runningStatus;
            ConnectionStatus = "BUSY";
            StatusText = startMessage;

            _nativeRunTask = Task.Run(() =>
            {
                try
                {
                    return nativeCall();
                }
                catch
                {
                    return int.MinValue;
                }
            });

            var exitCode = await _nativeRunTask.ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_stopRequested)
                {
                    _stopRequested = false;
                    ConnectionStatus = "CONNECTED";
                    StatusText = "Output stopped";
                }
                else if (exitCode == 0)
                {
                    ConnectionStatus = "CONNECTED";
                    StatusText = "Native call completed successfully";
                }
                else
                {
                    ConnectionStatus = "ERROR";
                    StatusText = exitCode == int.MinValue
                        ? "Native call failed (exception)"
                        : $"Native call failed (code {exitCode})";
                }

                if (OutputStatus == runningStatus)
                {
                    OutputStatus = "STOPPED";
                }
            });
        }
    }
}