#include "corelib_c_api.h"

#include "corelib.h"

extern "C" int corelib_start(int argc, const char* const* argv) {
	return corelib::start(argc, argv);
}

extern "C" int corelib_set_frequency_hz(double frequency_hz) {
	if (!(frequency_hz > 0.0)) {
		return 2;
	}
	if (!corelib::g_running.load()) {
		return 1;
	}
	corelib::g_base_frequency_hz.store(frequency_hz);
	corelib::g_current_frequency_hz.store(frequency_hz);
	return 0;
}

extern "C" double corelib_get_current_frequency_hz(void) {
	return corelib::g_current_frequency_hz.load();
}

extern "C" int corelib_get_accel_sample_ex(uint16_t* x, uint16_t* y, uint16_t* z, double* mapped_hz, int16_t* temp_centi_c) {
	if (!x || !y || !z || !mapped_hz || !temp_centi_c) {
		return 2;
	}
	if (!corelib::g_accel_last_valid.load()) {
		return 1;
	}
	*x = corelib::g_accel_last_x.load();
	*y = corelib::g_accel_last_y.load();
	*z = corelib::g_accel_last_z.load();
	*mapped_hz = corelib::g_accel_last_mapped_hz.load();
	*temp_centi_c = corelib::g_temp_last_centi_c.load();
	return 0;
}

extern "C" int corelib_get_accel_sample_ts(uint32_t* t_us, uint16_t* x, uint16_t* y, uint16_t* z, double* mapped_hz) {
	if (!t_us || !x || !y || !z || !mapped_hz) {
		return 2;
	}
	if (!corelib::g_accel_last_valid.load()) {
		return 1;
	}
	*t_us = corelib::g_accel_last_t_us.load();
	*x = corelib::g_accel_last_x.load();
	*y = corelib::g_accel_last_y.load();
	*z = corelib::g_accel_last_z.load();
	*mapped_hz = corelib::g_accel_last_mapped_hz.load();
	return 0;
}

extern "C" int corelib_get_accel_y_function(char* out_buf, int out_buf_len) {
	if (!out_buf || out_buf_len <= 0) {
		return 2;
	}
	return corelib::format_accel_y_function(out_buf, static_cast<size_t>(out_buf_len));
}

extern "C" int corelib_stop(void) {
	// If the loop isn't running, nothing to stop.
	if (!corelib::g_running.load()) {
		return 1;
	}
	corelib::g_running.store(false);
	corelib::g_current_frequency_hz.store(0.0);
	return 0;
}
