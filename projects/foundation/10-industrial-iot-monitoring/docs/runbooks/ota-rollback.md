# Runbook — OTA Firmware Rollback

## Symptoms
The device twin reports `otaStatus: rolledBack`, OTA verification fails, or the device reports an unexpected firmware version.

## Contain
1. Pause further firmware desired-version updates for the affected device type.
2. Do not issue arbitrary command payloads; only typed allow-listed `firmwareUpdate` requests are supported.
3. Preserve reported twin state, command audit entries, and device/gateway logs.

## Diagnose
1. Compare twin desired `firmwareVersion` with reported firmware version and `otaDetail`.
2. Check command status/audit trail and device connectivity.
3. Verify the requested version against the approved artifact manifest in a real deployment. This reference simulator treats names containing `bad` or `fail` as verification failure to exercise rollback.

## Recover
1. Confirm the device has retained the previous reported firmware version.
2. Set a known-good desired firmware version using `POST /api/v1/firmware/{deviceId}`.
3. Wait for download/verify/apply/report stages and verify the reported twin patch with the expected version.
4. Escalate to physical access if the device does not report after its approved recovery interval.

## Production hardening gap
The demonstration does not carry a firmware binary, cryptographic manifest, secure boot, or anti-rollback counter. A production OTA process requires signed artifacts, hardware root of trust, staged cohorts, health gates, and independently tested rollback procedures.
