#define STEP_PIN 3
#define DIR_PIN 4
#define ENABLE_PIN 5

bool running = false;
bool direction = true;

void setup() {
  pinMode(STEP_PIN, OUTPUT);
  pinMode(DIR_PIN, OUTPUT);
  pinMode(ENABLE_PIN, OUTPUT);

  digitalWrite(ENABLE_PIN, LOW); // Enable driver
  Serial.begin(9600);
}

void loop() {
  // Handle serial commands
  if (Serial.available()) {
    char cmd = Serial.read();

    if (cmd == 'f') {
      direction = true;
      running = true;
    }
    else if (cmd == 'b') {
      direction = false;
      running = true;
    }
    else if (cmd == 's') {
      running = false;
    }
  }

  digitalWrite(DIR_PIN, direction);

  if (running) {
    digitalWrite(STEP_PIN, HIGH);
    delayMicroseconds(800); // speed control
    digitalWrite(STEP_PIN, LOW);
    delayMicroseconds(800);
  }
}

