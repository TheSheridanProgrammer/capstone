#pragma once

#include <alsa/asoundlib.h>
#include <atomic>
#include <array>
#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cmath>
#include <cctype>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <fcntl.h>
#include <mutex>
#include <sstream>
#include <iomanip>
#include <iostream>
#include <string>
#include <termios.h>
#include <thread>
#include <unistd.h>
#include <unordered_set>
#include <vector>
#include <linux/i2c-dev.h>
#include <sys/ioctl.h>

namespace corelib {

// Optional hooks for integrating with an external UI/visualizer.
// This keeps the core audio/IO logic independent from any specific GUI implementation.
struct Callbacks {
	void* user = nullptr;
	void (*submitSamples)(void* user, const int16_t* interleaved, size_t frames) = nullptr;
	int (*shouldQuit)(void* user) = nullptr;
};

// Runtime state. Kept in the corelib namespace so it can be updated via the C API.
inline std::atomic<bool> g_running{true};
inline std::atomic<uint16_t> g_accel_baseline{0};
inline std::atomic<bool> g_accel_baseline_valid{false};
inline std::atomic<double> g_base_frequency_hz{100.0};
inline std::atomic<double> g_current_frequency_hz{0.0};

// Latest accelerometer sample (raw ADC counts from the XIAO firmware).
// Published by accel_reader_thread and readable via the C API.
inline std::atomic<uint16_t> g_accel_last_x{0};
inline std::atomic<uint16_t> g_accel_last_y{0};
inline std::atomic<uint16_t> g_accel_last_z{0};
inline std::atomic<int16_t> g_temp_last_centi_c{0};
inline std::atomic<uint32_t> g_accel_last_t_us{0};
inline std::atomic<double> g_accel_last_mapped_hz{0.0};
inline std::atomic<bool> g_accel_last_valid{false};

// Window of recent accel Y samples with timestamps (for fitting y(t)).
// Written by accel_reader_thread, readable via the C API.
struct AccelYSample {
	uint64_t t_us = 0;
	uint16_t y = 0;
};

inline std::mutex g_accel_y_window_mu;
inline std::array<AccelYSample, 100> g_accel_y_window{};
inline size_t g_accel_y_window_count = 0;
inline size_t g_accel_y_window_pos = 0;  // next write index (oldest when full)

inline bool g_accel_y_time_has_last = false;
inline uint32_t g_accel_y_time_last_t_us = 0;
inline uint64_t g_accel_y_time_extended_us = 0;

inline void record_accel_y_sample(uint32_t t_us, uint16_t y) {
	std::lock_guard<std::mutex> lock(g_accel_y_window_mu);
	// Extend 32-bit micros() to a monotonic 64-bit microsecond counter.
	if (!g_accel_y_time_has_last) {
		g_accel_y_time_has_last = true;
		g_accel_y_time_last_t_us = t_us;
		g_accel_y_time_extended_us = static_cast<uint64_t>(t_us);
	} else {
		const uint32_t delta = static_cast<uint32_t>(t_us - g_accel_y_time_last_t_us);  // wraps naturally
		g_accel_y_time_extended_us += static_cast<uint64_t>(delta);
		g_accel_y_time_last_t_us = t_us;
	}

	g_accel_y_window[g_accel_y_window_pos] = AccelYSample{g_accel_y_time_extended_us, y};
	g_accel_y_window_pos = (g_accel_y_window_pos + 1) % g_accel_y_window.size();
	if (g_accel_y_window_count < g_accel_y_window.size()) {
		++g_accel_y_window_count;
	}
}

// Formats a best-fit linear model for accel Y as a function of time over the last 100 samples.
// - Returns 0 on success, 1 if not enough samples yet, 2 on invalid args.
// - t is seconds since the first sample in the 100-sample window.
inline int format_accel_y_function(char* out_buf, size_t out_buf_len) {
	if (!out_buf || out_buf_len == 0) {
		return 2;
	}

	std::array<AccelYSample, 100> samples{};
	{
		std::lock_guard<std::mutex> lock(g_accel_y_window_mu);
		if (g_accel_y_window_count < samples.size()) {
			out_buf[0] = '\0';
			return 1;
		}
		const size_t n = samples.size();
		// Oldest sample is at g_accel_y_window_pos when the buffer is full.
		for (size_t i = 0; i < n; ++i) {
			samples[i] = g_accel_y_window[(g_accel_y_window_pos + i) % n];
		}
	}

	const size_t n = samples.size();
	const uint64_t t0_us = samples[0].t_us;

	double sum_t = 0.0;
	double sum_y = 0.0;
	double sum_tt = 0.0;
	double sum_ty = 0.0;

	for (size_t i = 0; i < n; ++i) {
		const double t = (static_cast<double>(samples[i].t_us - t0_us)) * 1e-6;
		const double y = static_cast<double>(samples[i].y);
		sum_t += t;
		sum_y += y;
		sum_tt += t * t;
		sum_ty += t * y;
	}

	const double mean_t = sum_t / static_cast<double>(n);
	const double mean_y = sum_y / static_cast<double>(n);

	const double denom = (static_cast<double>(n) * sum_tt) - (sum_t * sum_t);
	double m = 0.0;
	if (std::fabs(denom) > 1e-12) {
		m = ((static_cast<double>(n) * sum_ty) - (sum_t * sum_y)) / denom;
	}
	const double b = mean_y - m * mean_t;

	// R^2 (quality indicator)
	double ss_tot = 0.0;
	double ss_res = 0.0;
	for (size_t i = 0; i < n; ++i) {
		const double t = (static_cast<double>(samples[i].t_us - t0_us)) * 1e-6;
		const double y = static_cast<double>(samples[i].y);
		const double y_hat = m * t + b;
		const double dy = y - mean_y;
		ss_tot += dy * dy;
		const double err = y - y_hat;
		ss_res += err * err;
	}
	double r2 = 1.0;
	if (ss_tot > 1e-12) {
		r2 = 1.0 - (ss_res / ss_tot);
	}

	const double t_last = (static_cast<double>(samples[n - 1].t_us - t0_us)) * 1e-6;

	std::ostringstream oss;
	oss.setf(std::ios::fixed);
	oss << std::setprecision(3);
	oss << "y(t) = " << b << " + " << m << " t";
	oss << "   (t in s, n=" << n << ", span=" << t_last << "s, R^2=" << r2 << ")";

	const std::string s = oss.str();
	std::snprintf(out_buf, out_buf_len, "%s", s.c_str());
	return 0;
}

namespace {

void handle_sigint(int) {
	g_running.store(false);
}

std::string alsa_error(int err) {
	return std::string(snd_strerror(err));
}

std::string to_lower(std::string s) {
	std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) {
		return static_cast<char>(std::tolower(c));
	});
	return s;
}

void push_unique(std::vector<std::string>& out, std::unordered_set<std::string>& seen, std::string v) {
	if (v.empty()) {
		return;
	}
	if (seen.insert(v).second) {
		out.push_back(std::move(v));
	}
}

std::vector<std::string> enumerate_playback_hw_devices_prefer_usb() {
	std::vector<std::string> devices;
	int card = -1;
	if (snd_card_next(&card) < 0) {
		return devices;
	}
	while (card >= 0) {
		snd_ctl_t* ctl = nullptr;
		const std::string ctl_name = "hw:" + std::to_string(card);
		if (snd_ctl_open(&ctl, ctl_name.c_str(), 0) == 0 && ctl) {
			snd_ctl_card_info_t* info = nullptr;
			snd_ctl_card_info_alloca(&info);
			if (snd_ctl_card_info(ctl, info) == 0) {
				const char* id_c = snd_ctl_card_info_get_id(info);
				const char* name_c = snd_ctl_card_info_get_name(info);
				std::string id = id_c ? id_c : "";
				std::string name = name_c ? name_c : "";
				const std::string hay = to_lower(id + " " + name);

				// Skip Pi HDMI devices if possible.
				const bool looks_hdmi = (hay.find("hdmi") != std::string::npos) || (hay.find("vc4") != std::string::npos);
				if (!looks_hdmi) {
					int dev = -1;
					while (snd_ctl_pcm_next_device(ctl, &dev) == 0 && dev >= 0) {
						snd_pcm_info_t* pcminfo = nullptr;
						snd_pcm_info_alloca(&pcminfo);
						snd_pcm_info_set_device(pcminfo, static_cast<unsigned int>(dev));
						snd_pcm_info_set_subdevice(pcminfo, 0);
						snd_pcm_info_set_stream(pcminfo, SND_PCM_STREAM_PLAYBACK);
						if (snd_ctl_pcm_info(ctl, pcminfo) == 0) {
							devices.push_back("plughw:" + std::to_string(card) + "," + std::to_string(dev));
							break;
						}
					}
				}
			}
			snd_ctl_close(ctl);
		}
		if (snd_card_next(&card) < 0) {
			break;
		}
	}
	return devices;
}

bool try_parse_double(const char* s, double* out) {
	if (!s || !out) {
		return false;
	}
	char* end = nullptr;
	errno = 0;
	const double v = std::strtod(s, &end);
	if (errno != 0) {
		return false;
	}
	if (end == s || (end && *end != '\0')) {
		return false;
	}
	*out = v;
	return true;
}

struct AccelConfig {
	const char* i2c_dev = "/dev/i2c-1";
	int i2c_addr_7bit = 0x12;
	// Pi-side poll rate. If this is much lower than the firmware sample rate,
	// intermediate samples will be overwritten on the device before we read them.
	// 200 Hz is a good default that stays lightweight on the Pi.
	unsigned int poll_hz = 200;
};

struct AccelToHzConfig {
	// Raw ADC range from the XIAO firmware (12-bit resolution).
	uint16_t adc_min = 0;
	uint16_t adc_max = 4095;

	// Output frequency range.
	double hz_min = 100.0;
	double hz_max = 1000.0;

	// Base frequency used when accel equals baseline.
	double base_hz = 100.0;

	// Which axis to map. Defaulting to Z since many setups mount Z ~ gravity.
	enum class Axis { X, Y, Z } axis = Axis::Z;
};

uint16_t select_axis_value(uint16_t x, uint16_t y, uint16_t z, AccelToHzConfig::Axis axis) {
	switch (axis) {
		case AccelToHzConfig::Axis::X: return x;
		case AccelToHzConfig::Axis::Y: return y;
		case AccelToHzConfig::Axis::Z: return z;
	}
	return z;
}

double map_adc_to_hz(uint16_t adc, const AccelToHzConfig& cfg) {
	const double in_min = static_cast<double>(cfg.adc_min);
	const double in_max = static_cast<double>(cfg.adc_max);
	if (!(in_max > in_min)) {
		return cfg.hz_min;
	}
	const double x = static_cast<double>(adc);
	double t = (x - in_min) / (in_max - in_min);
	if (t < 0.0) t = 0.0;
	if (t > 1.0) t = 1.0;
	return cfg.hz_min + t * (cfg.hz_max - cfg.hz_min);
}

double accel_counts_to_hz(uint16_t x, uint16_t y, uint16_t z, const AccelToHzConfig& cfg = {}) {
	const uint16_t v = select_axis_value(x, y, z, cfg.axis);
	return map_adc_to_hz(v, cfg);
}

double accel_counts_to_hz_baseline(uint16_t x, uint16_t y, uint16_t z,
							const AccelToHzConfig& cfg,
							uint16_t baseline_counts,
							bool baseline_valid) {
	const uint16_t v = select_axis_value(x, y, z, cfg.axis);
	if (!baseline_valid) {
		return map_adc_to_hz(v, cfg);
	}
	const double in_min = static_cast<double>(cfg.adc_min);
	const double in_max = static_cast<double>(cfg.adc_max);
	if (!(in_max > in_min)) {
		return cfg.base_hz;
	}
	const double scale_hz_per_count = (cfg.hz_max - cfg.hz_min) / (in_max - in_min);
	const double delta = static_cast<double>(v) - static_cast<double>(baseline_counts);
	double hz = cfg.base_hz + delta * scale_hz_per_count;
	if (hz < cfg.hz_min) hz = cfg.hz_min;
	if (hz > cfg.hz_max) hz = cfg.hz_max;
	return hz;
}

int open_i2c_device(const AccelConfig& cfg) {
	const int fd = ::open(cfg.i2c_dev, O_RDWR);
	if (fd < 0) {
		return -1;
	}
	if (ioctl(fd, I2C_SLAVE, cfg.i2c_addr_7bit) < 0) {
		::close(fd);
		return -1;
	}
	return fd;
}
// Firmware protocol (XIAO ESP32-S3):
// - Pi reads 12 bytes: t0..t3, x_lo,x_hi, y_lo,y_hi, z_lo,z_hi, temp_lo,temp_hi
// - timestamp is micros() at sample time (32-bit, little-endian)
// - temp is centi-degrees Celsius (int16, little-endian)
bool read_accel_i2c(int fd, uint32_t* t_us, uint16_t* x, uint16_t* y, uint16_t* z, int16_t* temp_centi_c) {
	if (!x || !y || !z) {
		errno = EINVAL;
		return false;
	}
	uint8_t buf[12];
	size_t got = 0;
	while (got < sizeof(buf)) {
		const ssize_t n = ::read(fd, buf + got, sizeof(buf) - got);
		if (n < 0) {
			return false;
		}
		if (n == 0) {
			// Unusual for I2C; treat as failure.
			errno = EIO;
			return false;
		}
		got += static_cast<size_t>(n);
	}

	const uint32_t ts = static_cast<uint32_t>(buf[0]) |
	                  (static_cast<uint32_t>(buf[1]) << 8) |
	                  (static_cast<uint32_t>(buf[2]) << 16) |
	                  (static_cast<uint32_t>(buf[3]) << 24);
	if (t_us) {
		*t_us = ts;
	}

	*x = static_cast<uint16_t>(buf[4] | (static_cast<uint16_t>(buf[5]) << 8));
	*y = static_cast<uint16_t>(buf[6] | (static_cast<uint16_t>(buf[7]) << 8));
	*z = static_cast<uint16_t>(buf[8] | (static_cast<uint16_t>(buf[9]) << 8));
	if (temp_centi_c) {
		*temp_centi_c = static_cast<int16_t>(buf[10] | (static_cast<uint16_t>(buf[11]) << 8));
	}
	return true;
}

// Back-compat helper when caller doesn't care about timestamp/temp.
inline bool read_accel_i2c(int fd, uint16_t* x, uint16_t* y, uint16_t* z) {
	return read_accel_i2c(fd, nullptr, x, y, z, nullptr);
}

// Helper when caller wants temp but not timestamp.
inline bool read_accel_i2c(int fd, uint16_t* x, uint16_t* y, uint16_t* z, int16_t* temp_centi_c) {
	return read_accel_i2c(fd, nullptr, x, y, z, temp_centi_c);
}

// Reads accelerometer samples for ~duration, then returns the average of the selected axis.
// Prints "READING STOPPED" when finished.
uint16_t calibrate_accel_baseline(const AccelConfig& cfg,
								const AccelToHzConfig& axis_cfg,
								std::chrono::seconds duration = std::chrono::seconds(3)) {
	const int fd = open_i2c_device(cfg);
	if (fd < 0) {
		std::cerr << "Accel: calibration failed to open I2C device " << cfg.i2c_dev
		          << " addr 0x" << std::hex << cfg.i2c_addr_7bit << std::dec << "\n";
		std::cout << "READING STOPPED\n";
		return 0;
	}

	const unsigned int hz = cfg.poll_hz ? cfg.poll_hz : 200;
	const auto period = std::chrono::microseconds(1000000 / hz);
	const auto end_time = std::chrono::steady_clock::now() + duration;

	uint64_t sum = 0;
	uint32_t count = 0;
	while (g_running.load() && std::chrono::steady_clock::now() < end_time) {
		uint16_t x = 0, y = 0, z = 0;
		if (read_accel_i2c(fd, &x, &y, &z)) {
			sum += static_cast<uint64_t>(select_axis_value(x, y, z, axis_cfg.axis));
			++count;
		}
		std::this_thread::sleep_for(period);
	}

	::close(fd);
	std::cout << "READING STOPPED\n";
	if (count == 0) {
		return 0;
	}
	return static_cast<uint16_t>(sum / count);
}

speed_t baud_to_speed(int baud) {
	switch (baud) {
		case 9600:
			return B9600;
		case 19200:
			return B19200;
		case 38400:
			return B38400;
		case 57600:
			return B57600;
		case 115200:
		default:
			return B115200;
	}
}

int open_serial_port(const char* path, int baud) {
	const int fd = ::open(path, O_RDWR | O_NOCTTY | O_SYNC);
	if (fd < 0) {
		return -1;
	}

	termios tty{};
	if (tcgetattr(fd, &tty) != 0) {
		::close(fd);
		return -1;
	}

	cfmakeraw(&tty);
	const speed_t spd = baud_to_speed(baud);
	cfsetispeed(&tty, spd);
	cfsetospeed(&tty, spd);

	// 8N1
	tty.c_cflag = (tty.c_cflag & ~CSIZE) | CS8;
	tty.c_cflag |= (CLOCAL | CREAD);
	tty.c_cflag &= ~(PARENB | PARODD);
	tty.c_cflag &= ~CSTOPB;
	tty.c_cflag &= ~CRTSCTS;

	// Timeout reads so the thread can exit promptly.
	tty.c_cc[VMIN] = 0;
	tty.c_cc[VTIME] = 1;  // 0.1s

	if (tcsetattr(fd, TCSANOW, &tty) != 0) {
		::close(fd);
		return -1;
	}

	return fd;
}

bool parse_csv3(const std::string& line, long* a, long* b, long* c) {
	if (!a || !b || !c) {
		return false;
	}
	// Accept formats:
	//   x,y,z
	//   t_ms,x,y,z (we ignore t_ms)
	std::vector<long> vals;
	vals.reserve(4);
	const char* s = line.c_str();
	char* end = nullptr;
	while (*s) {
		errno = 0;
		const long v = std::strtol(s, &end, 10);
		if (errno != 0 || end == s) {
			break;
		}
		vals.push_back(v);
		if (*end == ',') {
			s = end + 1;
			continue;
		}
		s = end;
		if (*s == '\0' || *s == '\r' || *s == '\n') {
			break;
		}
		if (*s == ',') {
			s++;
		}
	}

	if (vals.size() == 3) {
		*a = vals[0];
		*b = vals[1];
		*c = vals[2];
		return true;
	}
	if (vals.size() == 4) {
		*a = vals[1];
		*b = vals[2];
		*c = vals[3];
		return true;
	}
	return false;
}

void accel_reader_thread(const AccelConfig cfg) {
	int fd = open_i2c_device(cfg);
	if (fd < 0) {
		std::cerr << "Accel: failed to open I2C device " << cfg.i2c_dev
		          << " addr 0x" << std::hex << cfg.i2c_addr_7bit << std::dec << "\n";
		return;
	}

	unsigned int hz = cfg.poll_hz ? cfg.poll_hz : 200;
	if (const char* env_hz = std::getenv("CORELIB_ACCEL_POLL_HZ")) {
		char* end = nullptr;
		errno = 0;
		const long v = std::strtol(env_hz, &end, 10);
		if (errno == 0 && end != env_hz && end && *end == '\0' && v > 0) {
			hz = static_cast<unsigned int>(v);
		}
	}
	const auto period = std::chrono::microseconds(1000000 / hz);

	while (g_running.load()) {
		uint32_t t_us = 0;
		uint16_t x = 0, y = 0, z = 0;
		int16_t temp_centi_c = 0;
		if (read_accel_i2c(fd, &t_us, &x, &y, &z, &temp_centi_c)) {
			AccelToHzConfig hz_cfg;
			hz_cfg.base_hz = g_base_frequency_hz.load();
			const uint16_t baseline = g_accel_baseline.load();
			const bool baseline_valid = g_accel_baseline_valid.load();
			const double hz = accel_counts_to_hz_baseline(x, y, z, hz_cfg, baseline, baseline_valid);
			g_accel_last_t_us.store(t_us);
			g_accel_last_x.store(x);
			g_accel_last_y.store(y);
			g_accel_last_z.store(z);
			g_temp_last_centi_c.store(temp_centi_c);
			g_accel_last_mapped_hz.store(hz);
			g_accel_last_valid.store(true);
			record_accel_y_sample(t_us, y);
			std::cout << "ADXL326 (XIAO I2C) t_us=" << t_us
			          << " x=" << x
			          << " y=" << y
			          << " z=" << z
			          << " temp=" << (static_cast<double>(temp_centi_c) / 100.0) << "C"
			          << " -> " << hz << " Hz\n"
			          << std::flush;
		} else {
			const int e = errno;
			std::cerr << "ADXL326 (XIAO I2C) read failed";
			if (e != 0) {
				std::cerr << " (errno=" << e << ": " << std::strerror(e) << ")";
			}
			std::cerr << "\n";
		}

		std::this_thread::sleep_for(period);
	}

	::close(fd);
}

}  // anonymous namespace

inline int start(int argc, const char* const* argv, const Callbacks* callbacks = nullptr) {
	// Allow start() to be called multiple times (e.g., from a UI).
	g_running.store(true);
	g_accel_baseline.store(0);
	g_accel_baseline_valid.store(false);
	g_base_frequency_hz.store(100.0);
	g_current_frequency_hz.store(0.0);
	g_accel_last_x.store(0);
	g_accel_last_y.store(0);
	g_accel_last_z.store(0);
	g_temp_last_centi_c.store(0);
	g_accel_last_t_us.store(0);
	g_accel_last_mapped_hz.store(0.0);
	g_accel_last_valid.store(false);
	{
		std::lock_guard<std::mutex> lock(g_accel_y_window_mu);
		g_accel_y_window_count = 0;
		g_accel_y_window_pos = 0;
		g_accel_y_time_has_last = false;
		g_accel_y_time_last_t_us = 0;
		g_accel_y_time_extended_us = 0;
	}

	printf("Starting...");

	// Preference: how long to hold each sweep frequency.
	const auto prefsec = std::chrono::seconds(3);

	// Device selection:
	// - If user provides a device via CLI, we respect it.
	// - Otherwise, try sensible fallbacks (including enumerated non-HDMI cards) so USB audio
	//   works on Pi 5 without manual config.
	std::string device_str = "plughw:CARD=Audio,DEV=0";
	double frequency_hz = 500.0;
	bool sweep_mode = false;
	double sweep_start_hz = 0.0;
	double sweep_end_hz = 0.0;
	double sweep_step_hz = 0.0;
	const unsigned int sample_rate = 48000;
	const unsigned int channels = 1;
	const snd_pcm_format_t format = SND_PCM_FORMAT_S16_LE;
	const double amplitude = 0.25;  // 0.0 .. 1.0 (keep modest to avoid clipping)
	const snd_pcm_uframes_t frames_per_period = 1024;

	// Args:
	//   ./tone500_gui                 -> default device, 500 Hz
	//   ./tone500_gui 333             -> default device, 333 Hz
	//   ./tone500_gui default 333     -> specified device, 333 Hz
	//   ./tone500_gui hw:0,0 333      -> specified device, 333 Hz
	//   ./tone500_gui 100 1000 50     -> sweep 100..1000 in 50 Hz steps (3s each)
	if (argc == 4) {
		double a = 0.0, b = 0.0, c = 0.0;
		if (try_parse_double(argv[1], &a) && try_parse_double(argv[2], &b) && try_parse_double(argv[3], &c)) {
			sweep_mode = true;
			sweep_start_hz = a;
			sweep_end_hz = b;
			sweep_step_hz = c;
			frequency_hz = sweep_start_hz;
		} else {
			std::cerr << "Usage: " << argv[0] << " [frequency_hz]\n"
			          << "   or: " << argv[0] << " [device] [frequency_hz]\n"
			          << "   or: " << argv[0] << " [start_hz] [end_hz] [step_hz]\n";
			return 2;
		}
	} else if (argc == 2) {
		double v = 0.0;
		if (try_parse_double(argv[1], &v)) {
			frequency_hz = v;
		} else {
			device_str = argv[1];
		}
	} else if (argc >= 3) {
		device_str = argv[1];
		double v = 0.0;
		if (try_parse_double(argv[2], &v)) {
			frequency_hz = v;
		} else {
			std::cerr << "Usage: " << argv[0] << " [frequency_hz]\n"
			          << "   or: " << argv[0] << " [device] [frequency_hz]\n";
			return 2;
		}
	}

	if (sweep_mode) {
		if (!(sweep_start_hz > 0.0) || !(sweep_end_hz > 0.0) || !(sweep_step_hz != 0.0)) {
			std::cerr << "Invalid sweep args: start=" << sweep_start_hz
			          << " end=" << sweep_end_hz
			          << " step=" << sweep_step_hz << "\n";
			return 2;
		}
		// Normalize step direction to move from start toward end.
		if (sweep_start_hz < sweep_end_hz && sweep_step_hz < 0.0) {
			sweep_step_hz = -sweep_step_hz;
		}
		if (sweep_start_hz > sweep_end_hz && sweep_step_hz > 0.0) {
			sweep_step_hz = -sweep_step_hz;
		}
		frequency_hz = sweep_start_hz;
	}

	if (!(frequency_hz > 0.0)) {
		std::cerr << "Invalid frequency: " << frequency_hz << "\n";
		return 2;
	}

	g_base_frequency_hz.store(frequency_hz);
	g_current_frequency_hz.store(frequency_hz);

	std::thread accel_thread;
	snd_pcm_t* pcm_handle = nullptr;

	auto cleanup = [&]() {
		g_running.store(false);
		g_current_frequency_hz.store(0.0);
		g_accel_last_valid.store(false);
		if (accel_thread.joinable()) {
			accel_thread.join();
		}
		if (pcm_handle) {
			snd_pcm_close(pcm_handle);
			pcm_handle = nullptr;
		}
	};

	auto fail = [&](int rc) {
		cleanup();
		return rc;
	};

	std::signal(SIGINT, handle_sigint);
	std::signal(SIGTERM, handle_sigint);

	// Self-config step: sample accelerometer for ~3 seconds and compute baseline.
	// This runs before audio playback starts.
	AccelConfig accel_cfg{};
	AccelToHzConfig axis_cfg{};  // default axis is Z
	axis_cfg.base_hz = frequency_hz;
	const uint16_t baseline = calibrate_accel_baseline(accel_cfg, axis_cfg, std::chrono::seconds(3));
	g_accel_baseline.store(baseline);
	g_accel_baseline_valid.store(true);
	std::cout << "Accel baseline (avg counts) = " << baseline << "\n";

	// Start accelerometer reader (ADXL326 -> Nano -> Pi). Prints x/y/z continuously.
	// Wiring must match:
	//   ADXL326: VCC->3.3V, GND->GND, XOUT->Nano A0, YOUT->A1, ZOUT->A2
	//   I2C (XIAO ESP32-S3): XIAO SDA=D4(GPIO5) <-> Pi SDA=GPIO2 (physical pin 3)
	//                       XIAO SCL=D5(GPIO6) <-> Pi SCL=GPIO3 (physical pin 5)
	//        GND <-> GND
	accel_thread = std::thread(accel_reader_thread, accel_cfg);

	// ALSA open with fallbacks.
	std::vector<std::string> candidates;
	std::unordered_set<std::string> seen;
	if (const char* env_dev = std::getenv("CORELIB_ALSA_DEVICE")) {
		push_unique(candidates, seen, env_dev);
	}
	// If caller already set device_str via CLI parsing, it will be tried first.
	push_unique(candidates, seen, device_str);
	push_unique(candidates, seen, "sysdefault");
	push_unique(candidates, seen, "default");
	for (const auto& dev : enumerate_playback_hw_devices_prefer_usb()) {
		push_unique(candidates, seen, dev);
	}
	push_unique(candidates, seen, "plughw:0,0");
	push_unique(candidates, seen, "hw:0,0");

	int err = -1;
	std::string opened_device;
	for (const auto& cand : candidates) {
		err = snd_pcm_open(&pcm_handle, cand.c_str(), SND_PCM_STREAM_PLAYBACK, 0);
		if (err >= 0) {
			opened_device = cand;
			break;
		}
	}
	if (err < 0) {
		std::cerr << "Failed to open ALSA device. Tried:";
		for (const auto& cand : candidates) {
			std::cerr << " '" << cand << "'";
		}
		std::cerr << ". Last error: " << alsa_error(err) << "\n";
		return fail(1);
	}
	if (opened_device != device_str) {
		std::cerr << "ALSA: requested '" << device_str << "', opened '" << opened_device << "'\n";
		device_str = opened_device;
	}

	snd_pcm_hw_params_t* hw_params = nullptr;
	snd_pcm_hw_params_alloca(&hw_params);
	err = snd_pcm_hw_params_any(pcm_handle, hw_params);
	if (err < 0) {
		std::cerr << "snd_pcm_hw_params_any failed: " << alsa_error(err) << "\n";
		return fail(1);
	}

	err = snd_pcm_hw_params_set_access(pcm_handle, hw_params, SND_PCM_ACCESS_RW_INTERLEAVED);
	if (err < 0) {
		std::cerr << "set_access failed: " << alsa_error(err) << "\n";
		return fail(1);
	}

	err = snd_pcm_hw_params_set_format(pcm_handle, hw_params, format);
	if (err < 0) {
		std::cerr << "set_format failed: " << alsa_error(err) << "\n";
		return fail(1);
	}

	unsigned int rate = sample_rate;
	err = snd_pcm_hw_params_set_rate_near(pcm_handle, hw_params, &rate, nullptr);
	if (err < 0) {
		std::cerr << "set_rate_near failed: " << alsa_error(err) << "\n";
		return fail(1);
	}
	if (rate != sample_rate) {
		std::cerr << "Note: requested " << sample_rate << " Hz, got " << rate << " Hz\n";
	}

	err = snd_pcm_hw_params_set_channels(pcm_handle, hw_params, channels);
	if (err < 0) {
		std::cerr << "set_channels failed: " << alsa_error(err) << "\n";
		return fail(1);
	}

	snd_pcm_uframes_t period_frames = frames_per_period;
	err = snd_pcm_hw_params_set_period_size_near(pcm_handle, hw_params, &period_frames, nullptr);
	if (err < 0) {
		std::cerr << "set_period_size_near failed: " << alsa_error(err) << "\n";
		return fail(1);
	}

	// Buffer size: a few periods to reduce underruns.
	snd_pcm_uframes_t buffer_frames = period_frames * 4;
	err = snd_pcm_hw_params_set_buffer_size_near(pcm_handle, hw_params, &buffer_frames);
	if (err < 0) {
		std::cerr << "set_buffer_size_near failed: " << alsa_error(err) << "\n";
		return fail(1);
	}

	err = snd_pcm_hw_params(pcm_handle, hw_params);
	if (err < 0) {
		std::cerr << "snd_pcm_hw_params failed: " << alsa_error(err) << "\n";
		return fail(1);
	}

	err = snd_pcm_prepare(pcm_handle);
	if (err < 0) {
		std::cerr << "snd_pcm_prepare failed: " << alsa_error(err) << "\n";
		return fail(1);
	}

	const double two_pi = 2.0 * M_PI;
	double phase_increment = two_pi * frequency_hz / static_cast<double>(rate);
	double phase = 0.0;

	// Interleaved frames; S16_LE so int16_t samples.
	std::vector<int16_t> buffer(static_cast<size_t>(period_frames) * channels);

	if (sweep_mode) {
		std::cout << "Sweeping " << sweep_start_hz << ".." << sweep_end_hz
		          << " Hz in " << sweep_step_hz << " Hz steps (" << prefsec.count()
		          << "s each) on ALSA device '" << device_str
		          << "' (Ctrl-C to stop)\n";
	} else {
		std::cout << "Playing " << frequency_hz << " Hz tone on ALSA device '" << device_str
		          << "' (Ctrl-C to stop)\n";
	}

	double current_frequency_hz = frequency_hz;
	g_current_frequency_hz.store(current_frequency_hz);
	auto sweep_last_change = std::chrono::steady_clock::now();

	while (g_running.load()) {
		if (sweep_mode) {
			const auto now = std::chrono::steady_clock::now();
			if (now - sweep_last_change >= prefsec) {
				sweep_last_change = now;
				const double next = current_frequency_hz + sweep_step_hz;
				const bool past_end = (sweep_step_hz > 0.0) ? (next > sweep_end_hz) : (next < sweep_end_hz);
				current_frequency_hz = past_end ? sweep_start_hz : next;
				g_base_frequency_hz.store(current_frequency_hz);
				g_current_frequency_hz.store(current_frequency_hz);
				phase_increment = two_pi * current_frequency_hz / static_cast<double>(rate);
			}
		}

		// Manual tone mode: allow external callers (GUI) to update the frequency in real-time.
		if (!sweep_mode) {
			const double requested_hz = g_base_frequency_hz.load();
			if (requested_hz > 0.0 && requested_hz != current_frequency_hz) {
				current_frequency_hz = requested_hz;
				g_current_frequency_hz.store(current_frequency_hz);
				phase_increment = two_pi * current_frequency_hz / static_cast<double>(rate);
			}
		}

		// Fill one period.
		for (snd_pcm_uframes_t i = 0; i < period_frames; ++i) {
			const double sample = std::sin(phase) * amplitude;
			const int16_t s = static_cast<int16_t>(std::lrint(sample * 32767.0));
			for (unsigned int ch = 0; ch < channels; ++ch) {
				buffer[static_cast<size_t>(i) * channels + ch] = s;
			}
			phase += phase_increment;
			if (phase >= two_pi) {
				phase -= two_pi;
			}
		}

		if (callbacks && callbacks->submitSamples) {
			callbacks->submitSamples(callbacks->user, buffer.data(), static_cast<size_t>(period_frames));
		}
		if (callbacks && callbacks->shouldQuit && callbacks->shouldQuit(callbacks->user)) {
			g_running.store(false);
		}

		snd_pcm_sframes_t written = snd_pcm_writei(pcm_handle, buffer.data(), period_frames);
		if (written == -EPIPE) {
			// XRUN (underrun)
			snd_pcm_prepare(pcm_handle);
			continue;
		}
		if (written < 0) {
			// Try to recover.
			written = snd_pcm_recover(pcm_handle, static_cast<int>(written), 1);
		}
		if (written < 0) {
			std::cerr << "ALSA write failed: " << alsa_error(static_cast<int>(written)) << "\n";
			break;
		}

		// Partial write: advance by writing remainder.
		snd_pcm_uframes_t remaining = period_frames - static_cast<snd_pcm_uframes_t>(written);
		while (remaining > 0 && g_running.load()) {
			const auto* ptr = buffer.data() + static_cast<size_t>(written) * channels;
			snd_pcm_sframes_t w = snd_pcm_writei(pcm_handle, ptr, remaining);
			if (w == -EPIPE) {
				snd_pcm_prepare(pcm_handle);
				break;
			}
			if (w < 0) {
				w = snd_pcm_recover(pcm_handle, static_cast<int>(w), 1);
			}
			if (w < 0) {
				std::cerr << "ALSA write failed: " << alsa_error(static_cast<int>(w)) << "\n";
				remaining = 0;
				break;
			}
			written += w;
			remaining -= static_cast<snd_pcm_uframes_t>(w);
		}
	}
	if (pcm_handle) {
		snd_pcm_drain(pcm_handle);
	}
	cleanup();
	std::cout << "Stopped.\n";
	return 0;
}

inline int start(int argc, char** argv) {
	return start(argc, const_cast<const char* const*>(argv));
}

}  // namespace corelib
