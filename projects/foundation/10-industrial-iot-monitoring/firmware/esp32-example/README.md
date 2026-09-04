# ESP32 Example Firmware

`industrial_iot_monitor.ino` is an illustrative Arduino C++ sketch for an ESP32 compressor node. It uses metric sensor names that match `TelemetryReading` exactly:

```json
{
  "deviceId": "esp32-cmp-01",
  "sequence": 42,
  "deviceTimestamp": "2026-09-01T10:00:00Z",
  "values": {
    "temperatureC": 58.2,
    "vibrationMmPerSecondRms": 2.3,
    "pressureBar": 7.4,
    "currentA": 26.0,
    "flowLitresPerMinute": 185.0,
    "tankLevelPercent": 64.0,
    "machineState": "Running"
  },
  "quality": "Good"
}
```

Its MQTT telemetry topic is `plants/{plantId}/devices/{deviceId}/telemetry`; it subscribes to `plants/{plantId}/devices/{deviceId}/commands/#`. The sketch requests QoS 1 telemetry, uses a monotonically increasing sequence, and illustrates a strict command allow-list. It does **not** execute arbitrary command strings.

## Required hardware/software work
- ESP32 board, sensor-specific drivers/calibration, Wi-Fi credentials, time synchronization, broker address, and an Arduino MQTT library compatible with `EspMQTTClient`.
- Secure production deployment should replace plaintext local MQTT with mutual TLS, a secure element/device certificate, signed OTA manifest/artifact verification, watchdog behavior, persistent sequence storage, offline flash buffering, and carefully engineered physical safety interlocks.

## Honest status
This sketch was **not compiled, flashed, or connected to physical hardware** on the build host. The host intentionally has no physical hardware or MQTT broker. The .NET simulator and in-process TCP broker are the executable/tested device path in this repository.
