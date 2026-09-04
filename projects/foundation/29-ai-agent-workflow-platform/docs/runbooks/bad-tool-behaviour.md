# Runbook: Bad Tool Behaviour

## Summary

Use this runbook when a registered tool errors, runs slowly, returns unexpected output, is requested without authorization or depends on an unavailable external service. The platform uses a static tool allow-list by design: changing, disabling or tightening a tool requires a code change and redeploy, not dynamic runtime loading.

## Symptoms

- Tool-error rate metrics increase.
- A run fails, halts or waits longer than expected after a tool step.
- The trace shows a tool-call error.
- The model requested a tool that is not allowed for the current step.
- `http_get` rejects a URL because of host allow-list or SSRF policy.
- A tool response is rejected as too large or schema-invalid.
- An external dependency is down, slow or rate-limited.
- Operators suspect a mutating tool may have been attempted twice.

## Diagnosis

1. Check the run trace.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/runs/{id}/trace" -Headers $headers
   ```

2. Locate the failed or suspicious tool call and record:

   - tool name;
   - step ID;
   - arguments;
   - result or structured error;
   - duration;
   - cost;
   - retry classification;
   - idempotency key for mutating tools.

3. Check tool metrics.

   ```powershell
   Invoke-RestMethod -Method Get -Uri "$base/api/v1/metrics" -Headers $headers
   ```

4. Identify the structured error category.

   | Error | Meaning |
   | --- | --- |
   | `invalid_arguments` | Arguments failed typed or JSON-schema validation |
   | `unauthorized_tool` | Tool is not allowed for this workflow step or authority context |
   | `unknown_tool` | Requested tool is not in the static registry |
   | `approval_required` | Tool requires approval before execution |
   | `output_too_large` | Tool output exceeded configured size caps |
   | `policy_violation` | Tool call violated platform policy, such as SSRF or host allow-list rules |
   | `rate_limited` | Tool or dependency refused the request due to rate limits |

5. For `http_get`, verify:

   - the requested host is on the allow-list;
   - the target is not a loopback, link-local, private or otherwise blocked address;
   - redirects did not leave the allow-list;
   - the request did not attempt to use `http_get` as arbitrary browsing.

6. For mutating tools, verify idempotency.

   Check whether the same idempotency key was reused and whether the tool recorded at-most-once completion. This is especially important for `update_ticket_status`, `create_refund_request` and `send_email`.

## Remediation

### Invalid or unexpected arguments

- Treat model-proposed arguments as untrusted input.
- Confirm the schema rejection is correct.
- If valid business input is being rejected, update the tool contract and tests.
- If invalid input is expected from a prompt, tighten the prompt, transform or schema.

### Unauthorized, unknown or approval-required tool

- Do not bypass the allow-list.
- Confirm the workflow step should or should not have access to the tool.
- For valid mutating or external actions, route through the approval flow.
- For invalid requests, leave the rejection in place and use the trace as evidence.

### Slow or failing tool

- Check whether the error is classified transient.
- Confirm retry with backoff is being applied only to transient failures.
- Check per-step and per-run timeout settings.
- If an external dependency is down, allow the run to fail, halt or hand off according to workflow policy.
- Resume only after the dependency or configuration issue is corrected.

### `http_get` policy rejection

- Verify the host allow-list and SSRF guard decision.
- If the destination is legitimate, update the allow-list in code and redeploy after review.
- Do not add broad wildcard access to work around one blocked request.

### Output too large

- Keep the cap in place.
- Reduce requested output, chunk the source input or transform the response into a bounded shape.
- Add an evaluation scenario if the oversized response exposed a new failure mode.

### Disabling or tightening a tool

Because tools are statically registered by design:

1. Change the tool registration, schema, policy or implementation in code.
2. Add or update unit and integration tests.
3. Run the targeted tests and evaluation scenarios.
4. Redeploy.

Do not attempt to remove or monkey-patch a tool dynamically at runtime.

## Escalation

Escalate when:

- a mutating tool has ambiguous completion and idempotency records are unclear;
- an unauthorized request appears to have executed;
- multiple workflows fail on the same tool after a deployment;
- `http_get` policy behaviour is uncertain;
- tool metrics and trace records disagree;
- a production dependency outage affects external tools.

Include run ID, tool name, step ID, structured error, trace excerpt, idempotency key and recent changes.

## Prevention

- Keep tool schemas narrow and typed.
- Keep the tool registry small and explicit.
- Require approval for high-risk mutating and external actions.
- Maintain SSRF guards and host allow-lists for `http_get`.
- Monitor tool-error rate, duration and rate-limit metrics.
- Include unauthorized-tool and output-too-large scenarios in the evaluation harness.
- Verify idempotency behaviour for every mutating tool before release.
