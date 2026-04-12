using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CapstoneController.Services;

internal static class SystemVolumeService
{
    internal static async Task<bool> TrySetVolumePercentAsync(int percent, CancellationToken cancellationToken)
    {
        percent = Math.Clamp(percent, 0, 100);

        if (!OperatingSystem.IsLinux())
            return false;

        // Prefer PulseAudio/PipeWire (common on Raspberry Pi OS).
        if (await TryRunAsync("pactl", $"set-sink-volume @DEFAULT_SINK@ {percent}%", cancellationToken).ConfigureAwait(false) == 0)
            return true;

        // ALSA fallback.
        if (await TryRunAsync("amixer", $"sset Master {percent}%", cancellationToken).ConfigureAwait(false) == 0)
            return true;

        // Alternate ALSA device path sometimes used with Pulse.
        if (await TryRunAsync("amixer", $"-D pulse sset Master {percent}%", cancellationToken).ConfigureAwait(false) == 0)
            return true;

        return false;
    }

    private static async Task<int> TryRunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            process.Start();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch
        {
            return -1;
        }
    }
}
