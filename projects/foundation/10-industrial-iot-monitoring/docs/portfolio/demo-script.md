# Five-minute Demo Script

1. Run `scripts\demo.ps1` or start the API at `http://localhost:5010`.
2. Open the dashboard and show fictional seeded compressor/chiller/tank status plus minute rollup sparklines.
3. Explain that the API process hosts a real loopback TCP MQTT broker on port 18830 and a simulated device/edge runtime.
4. Click **Kill network**. Wait for buffer depth to increase while local simulator readings continue.
5. Click **Restore network** and show the buffer drain. Explain the unique `(deviceId, sequence)` cloud key.
6. Queue a restart command for a compressor and show its typed audit/status API path.
7. Request a desired firmware version, show the twin, then describe the simulator's bad-version rollback path.
8. Point to `docs/anomaly-evaluation.md` and `docs/edge-buffering-test.md` for actual deterministic results.

Do not describe this as a deployed plant system or a safety-certified product.
