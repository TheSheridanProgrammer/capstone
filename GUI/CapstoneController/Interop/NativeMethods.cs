using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CapstoneController.Interop
{
    internal static class NativeMethods
    {
        private const string LibraryName = "corelib";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int corelib_start(int argc, IntPtr argv);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int corelib_stop();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int corelib_set_frequency_hz(double frequency_hz);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int corelib_get_accel_sample_ex(
            out ushort x,
            out ushort y,
            out ushort z,
            out double mapped_hz,
            out short temp_centi_c);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int corelib_get_accel_sample_ts(
            out uint t_us,
            out ushort x,
            out ushort y,
            out ushort z,
            out double mapped_hz);

        internal readonly record struct AccelSample(
            uint TimestampUs,
            ushort X,
            ushort Y,
            ushort Z,
            double MappedHz,
            short TempCentiC);

        private static bool _accelTsAvailable = true;

        internal static int Start(params string[] args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            IntPtr argvPtr = IntPtr.Zero;
            IntPtr[] stringPtrs = new IntPtr[args.Length];

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    stringPtrs[i] = Marshal.StringToHGlobalAnsi(args[i]);
                }

                argvPtr = Marshal.AllocHGlobal(IntPtr.Size * args.Length);

                for (int i = 0; i < args.Length; i++)
                {
                    Marshal.WriteIntPtr(argvPtr, i * IntPtr.Size, stringPtrs[i]);
                }

                return corelib_start(args.Length, argvPtr);
            }
            finally
            {
                if (argvPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(argvPtr);
                }

                for (int i = 0; i < stringPtrs.Length; i++)
                {
                    if (stringPtrs[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(stringPtrs[i]);
                    }
                }
            }
        }

        internal static int StartDefault()
        {
            return Start("corelib");
        }

        internal static int StartTone(double frequencyHz)
        {
            return Start(
                "corelib",
                frequencyHz.ToString(CultureInfo.InvariantCulture));
        }

        internal static int Stop()
        {
            return corelib_stop();
        }

        internal static int SetFrequencyHz(double frequencyHz)
        {
            return corelib_set_frequency_hz(frequencyHz);
        }

        internal static int StartWithDevice(string device, double frequencyHz)
        {
            if (string.IsNullOrWhiteSpace(device))
            {
                throw new ArgumentException("Device cannot be null or empty.", nameof(device));
            }

            return Start(
                "corelib",
                device,
                frequencyHz.ToString(CultureInfo.InvariantCulture));
        }

        internal static int StartSweep(double startHz, double endHz, double stepHz)
        {
            return Start(
                "corelib",
                startHz.ToString(CultureInfo.InvariantCulture),
                endHz.ToString(CultureInfo.InvariantCulture),
                stepHz.ToString(CultureInfo.InvariantCulture));
        }

        internal static int TryGetAccelSample(out AccelSample sample)
        {
            sample = default;

            // Prefer timestamped samples if the native lib exports it.
            if (_accelTsAvailable)
            {
                try
                {
                    var rcTs = corelib_get_accel_sample_ts(out var tUs, out var x, out var y, out var z, out var mappedHz);
                    if (rcTs != 0)
                        return rcTs;

                    // Temp is optional for the UI; best-effort.
                    var temp = (short)0;
                    if (corelib_get_accel_sample_ex(out _, out _, out _, out _, out var tempCentiC) == 0)
                        temp = tempCentiC;

                    sample = new AccelSample(tUs, x, y, z, mappedHz, temp);
                    return 0;
                }
                catch (EntryPointNotFoundException)
                {
                    _accelTsAvailable = false;
                }
            }

            // Fallback: no timestamp; caller can assume fixed sample rate.
            var rc = corelib_get_accel_sample_ex(out var fx, out var fy, out var fz, out var fmapped, out var ftemp);
            if (rc != 0)
                return rc;

            sample = new AccelSample(0, fx, fy, fz, fmapped, ftemp);
            return 0;
        }
    }
}