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

void loop() {
  updateBuzzer();
  static unsigned long lastIdleLog = 0;
  if (millis() - lastIdleLog >= 2000) {
    lastIdleLog = millis();
    Serial.print("ble="); Serial.print(bleOk); Serial.print(" advertising, x="); Serial.print(analogRead(PIN_X));
    Serial.print(" y="); Serial.println(analogRead(PIN_Y));
  }
  BLEDevice central = BLE.central();
  if (!central) return;

  calibrate();
  beep(BEEP_CONNECTED, 5);
  unsigned long lastSend = 0;
  unsigned char clicks = 0;
  bool pressed = false;
  bool recentered = false;
  unsigned long pressStart = 0;
  while (central.connected()) {
    updateBuzzer();
    if (stateChar.written()) {
      unsigned char state = stateChar.value();
      if (state == 1) beep(BEEP_ENABLED, 1);
      else if (state == 2) beep(BEEP_REFUSED, 3);
      else beep(BEEP_DISABLED, 3);
    }
    if (millis() - lastSend >= SEND_MS) {
      lastSend = millis();
      int rawX = analogRead(PIN_X);
      int rawY = analogRead(PIN_Y);
      bool down = rawX >= CLICK_THRESHOLD;
      if (down && !pressed) { pressed = true; recentered = false; pressStart = millis(); }
      if (down && pressed && !recentered && millis() - pressStart >= RECENTER_HOLD_MS) {
        recentered = true;  // re-center after release, once the stick is let go
      }
      if (!down && pressed) {
        pressed = false;
        if (recentered) {
          delay(300);
          calibrate();
          beep(BEEP_RECENTERED, 3);
        } else {
          clicks++;
        }
      }
      unsigned char packet[3];
      packet[0] = (unsigned char)(pressed ? 0 : toAxis(rawX, centerX));
      packet[1] = (unsigned char)(pressed ? 0 : toAxis(rawY, centerY));
      packet[2] = clicks;
      joyChar.setValue(packet, 3);
      Serial.print(rawX); Serial.print(' '); Serial.print(rawY); Serial.print(' ');
      Serial.print(centerX); Serial.print(' '); Serial.print(centerY); Serial.print(' '); Serial.println(clicks);
    }
  }
  digitalWrite(PIN_BUZZER, LOW);
  beepLength = 0;
}
