#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(__CYGWIN__)
	#define CORELIB_C_API __declspec(dllexport)
#else
	#define CORELIB_C_API __attribute__((visibility("default")))
#endif

// C-ABI entrypoint for P/Invoke and other FFI.
//
// argv is expected to point to an array of argc C strings (UTF-8/ANSI).
CORELIB_C_API int corelib_start(int argc, const char* const* argv);

// Optional: update the currently running tone frequency (Hz) in real-time.
// Returns 0 on success, non-zero if corelib is not running or args are invalid.
CORELIB_C_API int corelib_set_frequency_hz(double frequency_hz);

// Returns the current output frequency in Hz.
// When stopped/not yet started, returns 0.0.
CORELIB_C_API double corelib_get_current_frequency_hz(void);

// Returns the latest accelerometer sample.
// - x/y/z are raw ADC counts (0..4095) from the XIAO firmware.
// - mapped_hz is the accel->Hz mapping used for debug/telemetry.
// Returns 0 if a sample was written, 1 if no sample is available yet, 2 on invalid args.
CORELIB_C_API int corelib_get_accel_sample(uint16_t* x, uint16_t* y, uint16_t* z, double* mapped_hz);

// Extended variant that also returns the latest LM35 reading.
// - temp_centi_c is centi-degrees Celsius from firmware (e.g. 2534 => 25.34 C).
// Returns 0 if a sample was written, 1 if no sample is available yet, 2 on invalid args.
CORELIB_C_API int corelib_get_accel_sample_ex(uint16_t* x, uint16_t* y, uint16_t* z, double* mapped_hz, int16_t* temp_centi_c);

// Returns a best-fit y(t) model (based on the last 100 accel Y samples) as a human-readable string.
// - out_buf: caller-provided buffer (UTF-8/ANSI).
// - out_buf_len: buffer size in bytes.
// Returns 0 on success, 1 if not enough samples yet, 2 on invalid args.
CORELIB_C_API int corelib_get_accel_y_function(char* out_buf, int out_buf_len);

// Request corelib to stop playback/threads.
// Returns 0 if a stop was requested, 1 if corelib was not running.
CORELIB_C_API int corelib_stop(void);

#ifdef __cplusplus
}
#endif
