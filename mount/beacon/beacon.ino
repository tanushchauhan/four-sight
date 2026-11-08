// Rescue beacon for the AI-Thinker ESP32-CAM on the ESP32-CAM-MB baseboard.
// Grove buzzer: yellow on IO13, red on 3V3, black on GND. White wire stays unplugged.
// Dispatch is wireless. The board opens a Wi-Fi network named RESCUE.
// A phone joins that network, opens http://192.168.4.1, and taps Dispatch.
// Then the beacon blinks, beeps, and broadcasts a Find My advertisement.
// The iPhone Find My app will not list this board. Location reports, when Apple
// still returns them, are read by importing findmy_owner.json into Macless Haystack.

#include "esp_bt.h"
#include "esp_bt_main.h"
#include "esp_gap_ble_api.h"
#include "nvs_flash.h"
#include <DNSServer.h>
#include <WebServer.h>
#include <WiFi.h>
#include "findmy_key.h"

const int BuzzerPin = 13;
const int LampPin = 4;
const char *ApName = "RESCUE";
const IPAddress ApAddress(192, 168, 4, 1);

static uint8_t Advertisement[31] = {
    0x1e, 0xff, 0x4c, 0x00, 0x12, 0x19, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
static esp_bd_addr_t BeaconAddress = {0xFF, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF};
static esp_ble_adv_params_t AdvertisementParams = {
    .adv_int_min = 0x0140,
    .adv_int_max = 0x0140,
    .adv_type = ADV_TYPE_NONCONN_IND,
    .own_addr_type = BLE_ADDR_TYPE_RANDOM,
    .channel_map = ADV_CHNL_ALL,
    .adv_filter_policy = ADV_FILTER_ALLOW_SCAN_ANY_CON_ANY,
};
static bool RadioStarted = false;
static bool Dispatched = false;

WebServer server(80);
DNSServer dns;

void setOutputs(bool on) {
  digitalWrite(BuzzerPin, on ? HIGH : LOW);
  digitalWrite(LampPin, on ? HIGH : LOW);
}

void pulse(int milliseconds) {
  setOutputs(true);
  delay(milliseconds);
  setOutputs(false);
}

void setAddressFromKey(esp_bd_addr_t address, const uint8_t *publicKey) {
  address[0] = publicKey[0] | 0b11000000;
  address[1] = publicKey[1];
  address[2] = publicKey[2];
  address[3] = publicKey[3];
  address[4] = publicKey[4];
  address[5] = publicKey[5];
}

void setPayloadFromKey(uint8_t *payload, const uint8_t *publicKey) {
  memcpy(&payload[7], &publicKey[6], 22);
  payload[29] = publicKey[0] >> 6;
}

static void onGapEvent(esp_gap_ble_cb_event_t event, esp_ble_gap_cb_param_t *param) {
  if (event == ESP_GAP_BLE_ADV_DATA_RAW_SET_COMPLETE_EVT) {
    esp_ble_gap_start_advertising(&AdvertisementParams);
  }
  (void)param;
}

bool startRadio() {
  setAddressFromKey(BeaconAddress, FindMyPublicKey);
  setPayloadFromKey(Advertisement, FindMyPublicKey);

  esp_err_t err = nvs_flash_init();
  if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
    nvs_flash_erase();
    err = nvs_flash_init();
  }
  if (err != ESP_OK) {
    return false;
  }

  esp_bt_controller_config_t config = BT_CONTROLLER_INIT_CONFIG_DEFAULT();
  if (esp_bt_controller_init(&config) != ESP_OK) {
    return false;
  }
  if (esp_bt_controller_enable(ESP_BT_MODE_BLE) != ESP_OK) {
    return false;
  }
  if (esp_bluedroid_init() != ESP_OK) {
    return false;
  }
  if (esp_bluedroid_enable() != ESP_OK) {
    return false;
  }
  if (esp_ble_gap_register_callback(onGapEvent) != ESP_OK) {
    return false;
  }
  if (esp_ble_gap_set_rand_addr(BeaconAddress) != ESP_OK) {
    return false;
  }
  return esp_ble_gap_config_adv_data_raw(Advertisement, sizeof(Advertisement)) == ESP_OK;
}

void sendPage() {
  String page =
      "<!DOCTYPE html><html><head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
      "</head><body style=\"font-family:sans-serif;text-align:center;padding:2rem\">"
      "<h1>Rescue beacon</h1>";
  if (Dispatched) {
    page += "<p>Dispatched. It is beeping now.</p>";
  } else {
    page +=
        "<p>Waiting.</p>"
        "<form method=\"POST\" action=\"/dispatch\">"
        "<button style=\"font-size:1.4rem;padding:1rem 2rem\">Dispatch</button>"
        "</form>";
  }
  page += "</body></html>";
  server.send(200, "text/html", page);
}

void dispatchBeacon() {
  Dispatched = true;
  server.send(200, "text/plain", "dispatched");
}

void setup() {
  pinMode(BuzzerPin, OUTPUT);
  pinMode(LampPin, OUTPUT);
  setOutputs(false);

  esp_bt_controller_mem_release(ESP_BT_MODE_CLASSIC_BT);
  WiFi.mode(WIFI_AP);
  WiFi.softAPConfig(ApAddress, ApAddress, IPAddress(255, 255, 255, 0));
  WiFi.softAP(ApName);
  dns.start(53, "*", ApAddress);

  server.on("/", HTTP_GET, sendPage);
  server.on("/dispatch", HTTP_POST, dispatchBeacon);
  server.onNotFound([]() {
    server.sendHeader("Location", "/");
    server.send(302, "text/plain", "");
  });
  server.begin();
}

void loop() {
  dns.processNextRequest();
  server.handleClient();

  if (!Dispatched) {
    setOutputs(false);
    delay(10);
    return;
  }

  if (!RadioStarted) {
    RadioStarted = startRadio();
  }

  pulse(120);
  delay(150);
  pulse(120);
  delay(1400);
}
