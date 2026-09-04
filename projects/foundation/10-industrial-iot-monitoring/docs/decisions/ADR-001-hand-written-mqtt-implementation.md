# ADR-001: Hand-written scoped MQTT 3.1.1 implementation

## Context
The project must run without a broker and treats protocol competence as a portfolio differentiator. Devices, gateway, and tests need MQTT-style publish/subscribe semantics locally.

## Options
1. Add a full MQTT client/broker library.
2. Use only an in-memory event bus.
3. Implement the needed MQTT 3.1.1 subset over TCP in C#.

## Decision
Implement option 3: a binary codec plus loopback TCP broker/client supporting CONNECT/CONNACK, PUBLISH QoS 0/1 with PUBACK, SUBSCRIBE/SUBACK, `+`/`#` matching, PINGREQ/PINGRESP, DISCONNECT, retained messages, and last-will messages. The codec validates fixed-header flags and correct variable-length remaining-length encoding.

## Consequences
The repository is runnable without external messaging infrastructure and exposes protocol mechanics clearly. It also carries maintenance responsibility for the deliberately bounded implementation.

## Risks
A partial implementation could be mistaken for a general-purpose broker. README limitations and packet-level tests make the supported surface explicit.

## Alternatives
In production, retain `IMessageTransport` and use a hardened Mosquitto, Azure IoT Hub, or managed broker adapter with TLS, ACLs, persistent sessions, and operational monitoring.
