// Arduino 101 joystick controller for the PiCrawler bridge.
//
// Hardware (Grove base shield):
//   A0 connector: Grove thumb joystick (X on A0, Y on A1; clicking drives X to ~1023)
//   D3 connector: Grove buzzer (active, HIGH = sound)
//
// Bluetooth LE: advertises as "prada". The joystick characteristic notifies
// 3 bytes at 20 Hz: int8 turn, int8 forward, uint8 click count. The count goes
// up by one on each short click (on release), so the relay toggles exactly once
// per click and never misses one. Holding the click for 1.5 s re-centers the
// stick instead. The stick is also re-centered on every new connection, so leave
// it alone while it connects. The Pi relay writes 1 byte to the state
// characteristic (0 disabled, 1 enabled, 2 refused because the stick was not
// centered) so the buzzer can confirm what the robot is doing.

#include <CurieBLE.h>

const int PIN_X = A0;
const int PIN_Y = A1;
const int PIN_BUZZER = 3;

const int CLICK_THRESHOLD = 1000;  // X reading while the stick is pressed
const float AXIS_SPAN = 300.0;     // raw counts from center to full deflection
const unsigned long SEND_MS = 50;
const unsigned long RECENTER_HOLD_MS = 1500;

BLEService joyService("6b3f0001-8c1a-4d3e-9a0b-7d1f2c5e4a10");
BLECharacteristic joyChar("6b3f0002-8c1a-4d3e-9a0b-7d1f2c5e4a10", BLERead | BLENotify, 3);
BLEUnsignedCharCharacteristic stateChar("6b3f0003-8c1a-4d3e-9a0b-7d1f2c5e4a10", BLEWrite | BLEWriteWithoutResponse);

int bleOk = -1;
int centerX = 512;
int centerY = 512;

// Non-blocking buzzer: a pattern is a list of on/off durations in ms.
int beepPattern[6];
int beepLength = 0;
int beepIndex = 0;
unsigned long beepStarted = 0;

void beep(const int *pattern, int length) {
  for (int i = 0; i < length; i++) beepPattern[i] = pattern[i];
  beepLength = length;
  beepIndex = 0;
  beepStarted = millis();
  digitalWrite(PIN_BUZZER, HIGH);
}

void updateBuzzer() {
  if (beepIndex >= beepLength) return;
  if (millis() - beepStarted < (unsigned long)beepPattern[beepIndex]) return;
  beepIndex++;
  beepStarted = millis();
  digitalWrite(PIN_BUZZER, (beepIndex < beepLength && beepIndex % 2 == 0) ? HIGH : LOW);
}

const int BEEP_CONNECTED[] = {60, 80, 60, 80, 60};
const int BEEP_ENABLED[] = {200};
const int BEEP_DISABLED[] = {60, 80, 60};
const int BEEP_REFUSED[] = {400, 120, 400};   // stick not centered: re-center, then click again
const int BEEP_RECENTERED[] = {60, 60, 300};

int8_t toAxis(int raw, int center) {
  float value = (raw - center) / AXIS_SPAN;
  if (value > 1) value = 1;
  if (value < -1) value = -1;
  return (int8_t)(value * 127);
}

void calibrate() {
  long sumX = 0, sumY = 0;
  for (int i = 0; i < 32; i++) {
    sumX += analogRead(PIN_X);
    sumY += analogRead(PIN_Y);
    delay(5);
  }
  centerX = sumX / 32;
  centerY = sumY / 32;
}

void setup() {
  Serial.begin(115200);
  pinMode(PIN_BUZZER, OUTPUT);
  digitalWrite(PIN_BUZZER, LOW);
  calibrate();

  bleOk = BLE.begin();
  Serial.print("BLE.begin: ");
  Serial.println(bleOk);
  BLE.setLocalName("prada");  // short: flags + 128-bit UUID + name must fit in 31 bytes
  BLE.setAdvertisedService(joyService);
  joyService.addCharacteristic(joyChar);
  joyService.addCharacteristic(stateChar);
  BLE.addService(joyService);
  unsigned char zero[3] = {0, 0, 0};
  joyChar.setValue(zero, 3);
  BLE.advertise();
}

// USB packets: J,sequence,turn,forward,clicks at 20 Hz. Each transport
// establishes its own click baseline, so reconnecting never enables movement.
void loop() {
  BLE.poll();
  updateBuzzer();
  static bool wasBle = false, wasUsb = false;
  static unsigned long lastSend = 0, pressStart = 0, sequence = 0;
  static unsigned char clicks = 0;
  static bool pressed = false, recentered = false;
  BLEDevice central = BLE.central();
  bool ble = central && central.connected();
  bool usb = (bool)Serial;
  if (usb && !wasUsb) {
    calibrate();
    pressed = false;
    beep(BEEP_CONNECTED, 5);
  } else if (ble && !wasBle && !usb) {
    calibrate();
    pressed = false;
    beep(BEEP_CONNECTED, 5);
  }
  if (!ble && wasBle) BLE.advertise();
  wasBle = ble;
  wasUsb = usb;
  if (ble && stateChar.written()) {
    unsigned char state = stateChar.value();
    if (state == 1) beep(BEEP_ENABLED, 1);
    else if (state == 2) beep(BEEP_REFUSED, 3);
    else beep(BEEP_DISABLED, 3);
  }
  while (Serial.available()) {
    char state = Serial.read();
    if (state == 'E') beep(BEEP_ENABLED, 1);
    else if (state == 'R') beep(BEEP_REFUSED, 3);
    else if (state == 'D') beep(BEEP_DISABLED, 3);
  }
  if (millis() - lastSend < SEND_MS) return;
  lastSend = millis();
  int rawX = analogRead(PIN_X), rawY = analogRead(PIN_Y);
  bool down = rawX >= CLICK_THRESHOLD;
  if (down && !pressed) { pressed = true; recentered = false; pressStart = millis(); }
  if (down && pressed && millis() - pressStart >= RECENTER_HOLD_MS) recentered = true;
  if (!down && pressed) {
    pressed = false;
    if (recentered) {
      delay(300);
      calibrate();
      // Use the post-calibration readings rather than the old deflection.
      rawX = centerX; rawY = centerY;
      beep(BEEP_RECENTERED, 3);
    } else clicks++;
  }
  int8_t turn = pressed ? 0 : toAxis(rawX, centerX);
  int8_t forward = pressed ? 0 : toAxis(rawY, centerY);
  unsigned char packet[3] = {(unsigned char)turn, (unsigned char)forward, clicks};
  if (ble) joyChar.setValue(packet, 3);
  // Reserve enough space so USB cannot block BLE event handling.
  if (usb && Serial.availableForWrite() >= 48) {
    Serial.print("J,"); Serial.print(sequence++); Serial.print(',');
    Serial.print((int)turn); Serial.print(','); Serial.print((int)forward);
    Serial.print(','); Serial.println(clicks);
  }
}
