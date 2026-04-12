#include <Wire.h>

// XIAO ESP32-S3 firmware: read ADXL326 (analog X/Y/Z) and expose latest samples
// plus timestamp to Raspberry Pi over I2C.
//
// Wiring (ADXL326 -> XIAO ESP32-S3):
//   VCC  -> 3V3
//   GND  -> GND
//   XOUT -> D0 / A0
//   YOUT -> D1 / A1
//   ZOUT -> D2 / A2
//
// Wiring (LM35 -> XIAO ESP32-S3):
//   +Vs  -> 3V3
//   GND  -> GND
//   Vout -> D3 / A3
//
// Wiring (XIAO -> Raspberry Pi I2C):
//   XIAO SDA (GPIO5 / D4) -> Pi SDA (GPIO2 / physical pin 3)
//   XIAO SCL (GPIO6 / D5) -> Pi SCL (GPIO3 / physical pin 5)
//   XIAO GND              -> Pi GND
//
// Protocol:
// - Pi reads 12 bytes from I2C address 0x12
// - Data format (little-endian):
//     t0, t1, t2, t3, x_lo, x_hi, y_lo, y_hi, z_lo, z_hi, temp_lo, temp_hi
// - timestamp is micros() at the instant the sample is taken (uint32)
// - x/y/z are raw ADC counts (12-bit ADC in 16-bit containers)
// - temp is centi-degrees Celsius (int16, little-endian)

static const uint8_t I2C_ADDR = 0x12;
static const uint16_t ADC_MAX = 4095;
static const uint16_t ADC_VREF_MV = 3300;

// ADXL326 analog pins on XIAO
static const int PIN_X = D0;   // A0
static const int PIN_Y = D1;   // A1
static const int PIN_Z = D2;   // A2
static const int PIN_TEMP = D3; // A3 (LM35 Vout)

// XIAO I2C pins to Raspberry Pi
static const int I2C_SDA_PIN = D4;   // GPIO5
static const int I2C_SCL_PIN = D5;   // GPIO6

// Firmware sample rate.
// Note: The Pi must poll over I2C fast enough to drain samples; otherwise the queue will overflow.
static const unsigned int SAMPLE_HZ = 200;

// Simple FIFO so the Pi can drain samples even if it polls unevenly.
struct Sample {
  uint32_t t_us;
  uint16_t x;
  uint16_t y;
  uint16_t z;
  int16_t temp_centi_c;
};

static const uint8_t SAMPLE_Q_CAP = 64;
volatile Sample g_q[SAMPLE_Q_CAP];
volatile uint8_t g_q_head = 0; // next write
volatile uint8_t g_q_tail = 0; // next read

volatile uint32_t g_t_us = 0;
volatile uint16_t g_x = 0;
volatile uint16_t g_y = 0;
volatile uint16_t g_z = 0;
volatile int16_t g_temp_centi_c = 0;

static inline bool q_is_empty() {
  return g_q_head == g_q_tail;
}

static inline bool q_is_full() {
  return (uint8_t)(g_q_head + 1) == g_q_tail;
}

static inline void q_push(const Sample& s) {
  // Drop oldest on overflow (keeps the most recent motion).
  if (q_is_full()) {
    g_q_tail = (uint8_t)(g_q_tail + 1);
  }
  g_q[g_q_head] = s;
  g_q_head = (uint8_t)(g_q_head + 1);
}

static inline bool q_pop(Sample* out) {
  if (!out) return false;
  if (q_is_empty()) return false;
  *out = g_q[g_q_tail];
  g_q_tail = (uint8_t)(g_q_tail + 1);
  return true;
}

void onI2CRequest() {
  Sample s{};

  noInterrupts();
  // Prefer draining the FIFO; if empty, fall back to the latest snapshot.
  if (!q_pop(&s)) {
    s.t_us = g_t_us;
    s.x = g_x;
    s.y = g_y;
    s.z = g_z;
    s.temp_centi_c = g_temp_centi_c;
  }
  interrupts();

  uint8_t out[12];
  out[0] = (uint8_t)(s.t_us & 0xFF);
  out[1] = (uint8_t)((s.t_us >> 8) & 0xFF);
  out[2] = (uint8_t)((s.t_us >> 16) & 0xFF);
  out[3] = (uint8_t)((s.t_us >> 24) & 0xFF);

  out[4] = (uint8_t)(s.x & 0xFF);
  out[5] = (uint8_t)((s.x >> 8) & 0xFF);

  out[6] = (uint8_t)(s.y & 0xFF);
  out[7] = (uint8_t)((s.y >> 8) & 0xFF);

  out[8] = (uint8_t)(s.z & 0xFF);
  out[9] = (uint8_t)((s.z >> 8) & 0xFF);

  out[10] = (uint8_t)(s.temp_centi_c & 0xFF);
  out[11] = (uint8_t)((s.temp_centi_c >> 8) & 0xFF);

  Wire.write(out, sizeof(out));
}

void setup() {
  Serial.begin(115200);
  delay(50);

  analogReadResolution(12); // 0..4095

  // Prime ADC
  (void)analogRead(PIN_X);
  (void)analogRead(PIN_Y);
  (void)analogRead(PIN_Z);
  (void)analogRead(PIN_TEMP);
  delay(10);

#if defined(ARDUINO_ARCH_ESP32)
  Wire.begin((uint8_t)I2C_ADDR, I2C_SDA_PIN, I2C_SCL_PIN, 100000);
#else
  Wire.begin((uint8_t)I2C_ADDR);
#endif
  Wire.onRequest(onI2CRequest);

  Serial.print("I2C slave up at 0x");
  Serial.println(I2C_ADDR, HEX);

  Serial.print("Sampling at ");
  Serial.print(SAMPLE_HZ);
  Serial.println(" Hz");
}

void loop() {
  static unsigned long last_us = 0;
  const unsigned long period_us = 1000000UL / SAMPLE_HZ;
  const unsigned long now = micros();

  if ((unsigned long)(now - last_us) < period_us) {
    return;
  }

  last_us += period_us;

  const uint32_t t_us = micros();
  const uint16_t x = (uint16_t)analogRead(PIN_X);
  const uint16_t y = (uint16_t)analogRead(PIN_Y);
  const uint16_t z = (uint16_t)analogRead(PIN_Z);
  const uint16_t temp_adc = (uint16_t)analogRead(PIN_TEMP);

  // LM35 output is 10 mV / degC. Convert ADC code -> mV -> centi-degC.
  const uint32_t temp_mv = ((uint32_t)temp_adc * ADC_VREF_MV + (ADC_MAX / 2)) / ADC_MAX;
  int32_t temp_centi_c = (int32_t)temp_mv * 10;
  if (temp_centi_c > 32767) {
    temp_centi_c = 32767;
  }

  noInterrupts();
  g_t_us = t_us;
  g_x = x;
  g_y = y;
  g_z = z;
  g_temp_centi_c = (int16_t)temp_centi_c;

  // Queue the sample for the Pi to drain.
  Sample s;
  s.t_us = t_us;
  s.x = x;
  s.y = y;
  s.z = z;
  s.temp_centi_c = (int16_t)temp_centi_c;
  q_push(s);
  interrupts();
}