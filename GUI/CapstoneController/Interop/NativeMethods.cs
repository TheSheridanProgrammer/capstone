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
    }
}