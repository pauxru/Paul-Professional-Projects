# Feature Flag Service — Portfolio Summary

## Classification
Self-directed engineering case study and production-style prototype.

## Problem
Release control needs more than a database boolean: it needs stable cohorts, explainable evaluation, governance, emergency mitigation, and clients that continue operating during control-plane failure.

## What was built
A .NET 10 API/control plane, SQLite persistence, SSE/ETag configuration delivery, an offline-capable .NET SDK, and a small ASP.NET Core consumer.

## Engineering evidence
- Shared evaluator produces server/SDK parity for 10,000 committed fixture keys.
- SHA-256 basis-point bucketing is tested for determinism, distribution sanity, and sticky 10%→20% expansion.
- SQLite-backed integration tests exercise JWT policies, client/server SDK-key filtering, audit/revert, approvals, analytics, and SSE refresh.

## Production evolution
Replace local broadcaster with durable pub/sub/outbox, use OIDC and secure key storage/rotation, normalize/query rule data where needed, and add multi-node telemetry/retention controls.
