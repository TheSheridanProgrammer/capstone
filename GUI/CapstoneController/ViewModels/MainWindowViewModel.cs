using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CapstoneController.Interop;
using CapstoneController.Services;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CapstoneController.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private Task<int>? _nativeRunTask;
        private CancellationTokenSource? _volumeCts;

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
        private double volumePercent = 50;

        public IAsyncRelayCommand SetFrequencyCommand { get; }
        public IAsyncRelayCommand StartSweepCommand { get; }
        public IAsyncRelayCommand StartOutputCommand { get; }
        public IRelayCommand StopCommand { get; }
        public IRelayCommand ShowOutputCommand { get; }

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
                StatusText = "Stop requested (native stop not exposed)";
                return;
            }

            StatusText = "Output stopped";
        }

        private void TogglePreview()
        {
            IsShowingOutput = !IsShowingOutput;
            StatusText = IsShowingOutput ? "Output preview shown" : "Output preview hidden";
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
                if (exitCode == 0)
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