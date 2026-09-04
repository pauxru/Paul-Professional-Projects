/*
 * Illustrative Arduino ESP32 firmware for the Iiot.Device wire format.
 * Dependencies: WiFi, ESP32 MQTT Client (EspMQTTClient), ArduinoJson,
 * and hardware-specific sensor drivers. See README.md: this sketch was
 * not compiled or flashed on the build host.
 */
#include <WiFi.h>
#include <EspMQTTClient.h>
#include <ArduinoJson.h>

constexpr char WIFI_SSID[] = "replace-with-wifi-ssid";
constexpr char WIFI_PASSWORD[] = "replace-with-wifi-password";
constexpr char MQTT_HOST[] = "192.168.1.10";
constexpr uint16_t MQTT_PORT = 1883;
constexpr char DEVICE_ID[] = "esp32-cmp-01";
constexpr char PLANT_ID[] = "plant-a";

EspMQTTClient mqtt(
  WIFI_SSID, WIFI_PASSWORD, MQTT_HOST, "", "", DEVICE_ID, MQTT_PORT);

uint32_t sequenceNumber = 0;
unsigned long nextSampleAt = 0;

// Replace with calibrated sensor-driver calls. These values are metric units.
float readTemperatureC() { return 58.2f; }
float readVibrationMmPerSecondRms() { return 2.3f; }
float readPressureBar() { return 7.4f; }
float readCurrentA() { return 26.0f; }
float readFlowLitresPerMinute() { return 185.0f; }
float readTankLevelPercent() { return 64.0f; }

String iso8601Utc() {
  // A production sketch obtains UTC through SNTP and formats it precisely.
  // This placeholder shows the API's expected ISO 8601 field shape.
  return "2026-09-01T10:00:00Z";
}

void publishTelemetry() {
  JsonDocument message;
  message["deviceId"] = DEVICE_ID;
  message["sequence"] = sequenceNumber++;
  message["deviceTimestamp"] = iso8601Utc();
  JsonObject values = message["values"].to<JsonObject>();
  values["temperatureC"] = readTemperatureC();
  values["vibrationMmPerSecondRms"] = readVibrationMmPerSecondRms();
  values["pressureBar"] = readPressureBar();
  values["currentA"] = readCurrentA();
  values["flowLitresPerMinute"] = readFlowLitresPerMinute();
  values["tankLevelPercent"] = readTankLevelPercent();
  values["machineState"] = "Running";
  message["quality"] = "Good";

  char payload[512];
  const size_t length = serializeJson(message, payload, sizeof(payload));
  if (length == 0 || length >= sizeof(payload)) {
    return; // A real device records a local diagnostic counter here.
  }

  const String topic = String("plants/") + PLANT_ID + "/devices/" + DEVICE_ID + "/telemetry";
  // QoS 1 lets the platform's broker issue PUBACK. The cloud remains idempotent by sequence.
  mqtt.publish(topic.c_str(), payload, false, 1);
}

void onConnectionEstablished() {
  const String commandTopic = String("plants/") + PLANT_ID + "/devices/" + DEVICE_ID + "/commands/#";
  mqtt.subscribe(commandTopic, [](const String& topic, const String& payload) {
    // Parse only strict, typed commands such as restart, setSamplingInterval,
    // setTargetTemperature, and firmwareUpdate. Never execute arbitrary payload text.
    JsonDocument command;
    if (deserializeJson(command, payload) != DeserializationError::Ok) return;
    const char* type = command["commandType"] | "";
    if (strcmp(type, "restart") == 0) {
      // Guarded application-specific restart implementation.
    } else if (strcmp(type, "setSamplingInterval") == 0) {
      const int seconds = command["parameters"]["seconds"] | 0;
      if (seconds < 1 || seconds > 3600) return;
    } else if (strcmp(type, "firmwareUpdate") == 0) {
      // Download only a signed manifest/artifact over TLS in production.
    }
  });
}

void setup() {
  Serial.begin(115200);
  mqtt.enableDebuggingMessages(false);
}

void loop() {
  mqtt.loop();
  if (mqtt.isConnected() && millis() >= nextSampleAt) {
    publishTelemetry();
    nextSampleAt = millis() + 15000;
  }
}
