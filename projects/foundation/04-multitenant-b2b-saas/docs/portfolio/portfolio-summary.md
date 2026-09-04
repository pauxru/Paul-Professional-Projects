# Portfolio Summary

## Project

FieldOps Multi-Tenant B2B SaaS Platform — self-directed engineering case study.

## Problem

Shared B2B applications need to isolate organizations while roles, plans, usage, feature exposure and payment state keep changing. A single missed tenant predicate, stale permission or replayed webhook can create an incident.

## Built

A .NET 10 modular monolith for field assets, work orders, scored inspections, membership/invitations, entitlements, metering, flags, simulated billing, dunning, audit and a functional dashboard.

## Principal engineering signals

- Tenant resolution across JWT/header/subdomain with ambiguity rejection.
- Every tenant entity protected by EF query filters and write interception.
- Deliberately unsafe repository test proves defence in depth.
- Live permission lookup and tested role-cache invalidation.
- Deterministic flag rollout and tenant-safe cache keys.
- Raw-body HMAC webhook validation, timestamp window and replay receipts.
- Zero-infrastructure Release build/tests with SQLite in-memory integration coverage.

## Honest scope

This is a self-directed reference implementation. It has no real clients, users, revenue, production traffic, uptime result or compliance certification. Docker artifacts are unverified on the build host.
