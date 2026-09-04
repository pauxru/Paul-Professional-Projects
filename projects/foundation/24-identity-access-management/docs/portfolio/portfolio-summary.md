# Portfolio Summary

## Project

Northstar Identity & Access Management Administration Portal — self-directed engineering case study.

## Problem

Access governance fails when an enterprise cannot explain inherited access, enforce toxic combinations, expire temporary privilege, certify effective permissions, or detect target-side drift.

## Built

A .NET 10 modular-monolith IGA reference implementation with:

- joiner-mover-leaver automation;
- dynamic groups and cycle-safe hierarchical roles;
- explainable RBAC/ABAC with deny-wins precedence;
- preventive/detective SoD and expiring exceptions;
- sequential/parallel/delegated/escalated approvals;
- JIT elevation whose expiry changes real authorization decisions;
- application/department/risk certification campaigns and deadline auto-revocation;
- simulated HR/target connectors, retries, quarantine, orphan and rogue-grant reconciliation;
- append-only hash-linked audit, access derivation reports, SCIM-shaped API, and administration UI.

## Strongest engineering signal

The access-profile report preserves every path from user through direct grant, dynamic/static group, nested roles, entitlement, and JIT ticket. Certification revocation remains effective across all those paths.

## Verification

The solution builds and tests offline with .NET 10 and SQLite. Real final output is in `docs/test-results.md`. Docker files are authored but unverified because Docker was unavailable on the build host.

## Non-claims

This is not client work, not deployed, and not a compliance-certified product.
