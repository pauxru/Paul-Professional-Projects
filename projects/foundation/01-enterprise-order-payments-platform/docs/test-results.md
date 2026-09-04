# Test results

Captured directly from the live `dotnet build -c Release` and `dotnet test -c Release`
invocations at the end of the initial build. No editing.

## Host

- Windows, .NET SDK 10.0.400, target `net10.0`
- SQLite via `Microsoft.EntityFrameworkCore.Sqlite`
- No Docker, no Postgres, no Redis, no broker

## dotnet build -c Release

```
  Determining projects to restore...
  All projects are up-to-date for restore.
  Contoso.Payments.Domain -> C:\Users\rukwaropaul\Downloads\DEV\Projects\01-enterprise-order-payments-platform\src\Contoso.Payments.Domain\bin\Release\net10.0\Contoso.Payments.Domain.dll
  Contoso.Payments.Application -> C:\Users\rukwaropaul\Downloads\DEV\Projects\01-enterprise-order-payments-platform\src\Contoso.Payments.Application\bin\Release\net10.0\Contoso.Payments.Application.dll
  Contoso.Payments.Infrastructure -> C:\Users\rukwaropaul\Downloads\DEV\Projects\01-enterprise-order-payments-platform\src\Contoso.Payments.Infrastructure\bin\Release\net10.0\Contoso.Payments.Infrastructure.dll
  Contoso.Payments.UnitTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\01-enterprise-order-payments-platform\tests\Contoso.Payments.UnitTests\bin\Release\net10.0\Contoso.Payments.UnitTests.dll
  Contoso.Payments.Api -> C:\Users\rukwaropaul\Downloads\DEV\Projects\01-enterprise-order-payments-platform\src\Contoso.Payments.Api\bin\Release\net10.0\Contoso.Payments.Api.dll
  Contoso.Payments.IntegrationTests -> C:\Users\rukwaropaul\Downloads\DEV\Projects\01-enterprise-order-payments-platform\tests\Contoso.Payments.IntegrationTests\bin\Release\net10.0\Contoso.Payments.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.37

```

## dotnet test -c Release

```
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\01-enterprise-order-payments-platform\tests\Contoso.Payments.IntegrationTests\bin\Release\net10.0\Contoso.Payments.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\rukwaropaul\Downloads\DEV\Projects\01-enterprise-order-payments-platform\tests\Contoso.Payments.UnitTests\bin\Release\net10.0\Contoso.Payments.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
[xUnit.net 00:00:00.00] xUnit.net VSTest Adapter v3.1.4+50e68bbb8b (64-bit .NET 10.0.11)
[xUnit.net 00:00:00.00] xUnit.net VSTest Adapter v3.1.4+50e68bbb8b (64-bit .NET 10.0.11)
[xUnit.net 00:00:00.09]   Discovering: Contoso.Payments.UnitTests
[xUnit.net 00:00:00.11]   Discovering: Contoso.Payments.IntegrationTests
[xUnit.net 00:00:00.14]   Discovered:  Contoso.Payments.UnitTests
[xUnit.net 00:00:00.16]   Discovered:  Contoso.Payments.IntegrationTests
[xUnit.net 00:00:00.17]   Starting:    Contoso.Payments.UnitTests
[xUnit.net 00:00:00.19]   Starting:    Contoso.Payments.IntegrationTests
  Passed Contoso.Payments.UnitTests.Domain.HmacSha256WebhookSignatureVerifierTests.Verify_returns_true_for_valid_signature [7 ms]
  Passed Contoso.Payments.UnitTests.Domain.PaymentIntentTests.MarkAuthorized_twice_throws [10 ms]
  Passed Contoso.Payments.UnitTests.Application.HashingTests.Sha256_of_empty_string_matches_known_value [7 ms]
  Passed Contoso.Payments.UnitTests.Domain.MoneyTests.GreaterThan_across_currencies_throws [7 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.Cancel_is_idempotent [12 ms]
  Passed Contoso.Payments.UnitTests.Domain.InventoryItemTests.Available_equals_OnHand_minus_Reserved [12 ms]
  Passed Contoso.Payments.UnitTests.Application.SettlementFileTests.Generator_can_omit_one_row_for_MissingInProvider [12 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.NewOrder_starts_in_Draft [< 1 ms]
  Passed Contoso.Payments.UnitTests.Application.SettlementFileTests.Generator_appends_extra_row_for_MissingInternally [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.HmacSha256WebhookSignatureVerifierTests.Verify_returns_false_for_tampered_body [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.MoneyTests.Rejects_unsupported_currency [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.InventoryItemTests.Commit_deducts_from_OnHand [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.HmacSha256WebhookSignatureVerifierTests.Verify_returns_false_for_malformed_header [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.MoneyTests.Of_normalises_currency_case [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.HmacSha256WebhookSignatureVerifierTests.Verify_returns_false_for_missing_header [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.PaymentIntentTests.Attempts_accumulate [1 ms]
  Passed Contoso.Payments.UnitTests.Domain.HmacSha256WebhookSignatureVerifierTests.Verify_returns_false_for_expired_timestamp [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.HmacSha256WebhookSignatureVerifierTests.Verify_returns_false_for_wrong_secret [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.InventoryItemTests.Committing_a_released_reservation_throws [1 ms]
  Passed Contoso.Payments.UnitTests.Domain.InventoryItemTests.Reserve_zero_or_negative_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.MarkFulfilled_only_valid_from_Paid [2 ms]
  Passed Contoso.Payments.UnitTests.Domain.InventoryItemTests.Version_increments_on_state_change [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.InventoryItemTests.Release_missing_reservation_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.InventoryItemTests.Reserve_more_than_available_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.InventoryItemTests.Release_returns_stock_to_pool [< 1 ms]
  Passed Contoso.Payments.UnitTests.Application.SettlementFileTests.Parser_reads_generated_baseline [4 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.Cancel_from_Fulfilled_throws [1 ms]
  Passed Contoso.Payments.UnitTests.Application.HashingTests.Sha256_of_different_input_differs [4 ms]
  Passed Contoso.Payments.UnitTests.Application.HashingTests.Sha256_is_deterministic [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.PaymentIntentTests.New_intent_starts_in_Requires [3 ms]
  Passed Contoso.Payments.UnitTests.Domain.PaymentIntentTests.Missing_idempotency_key_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.PaymentIntentTests.MarkVoided_only_valid_from_Authorized [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.MoneyTests.To_and_from_minor_units_round_trip [7 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.Partial_refund_moves_to_PartiallyRefunded [3 ms]
  Passed Contoso.Payments.UnitTests.Domain.PaymentIntentTests.MarkCaptured_only_valid_from_Authorized [1 ms]
  Passed Contoso.Payments.UnitTests.Domain.MoneyTests.Multiply_by_negative_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.Cancel_valid_from_Draft_Pending_AwaitingPayment_and_Paid [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.MoneyTests.Add_of_different_currency_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.PaymentIntentTests.MarkFailed_from_Captured_throws [1 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.Refund_exceeding_total_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.PaymentIntentTests.MarkAuthorized_moves_to_Authorized [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.Full_refund_moves_to_Refunded [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.MoneyTests.Subtract_reduces_amount [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.MarkPaid_only_valid_from_AwaitingPayment [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.MoneyTests.Add_of_same_currency_sums [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.MoneyTests.Multiply_by_quantity [< 1 ms]
  Passed Contoso.Payments.UnitTests.Application.SettlementFileTests.Generator_injects_amount_mismatch [6 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.MarkPending_on_empty_order_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.MarkFulfilled_from_Pending_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Application.SettlementFileTests.Generator_duplicates_one_row_for_DuplicateInProvider [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.MoveToAwaitingPayment_from_Draft_throws [< 1 ms]
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.MarkPending_moves_Draft_to_Pending [< 1 ms]
[xUnit.net 00:00:00.30]   Finished:    Contoso.Payments.UnitTests
  Passed Contoso.Payments.UnitTests.Domain.OrderStateMachineTests.MoveToAwaitingPayment_only_valid_from_Pending [< 1 ms]

Test Run Successful.
Total tests: 53
     Passed: 53
 Total time: 0.9373 Seconds
warn: Contoso.Payments.Infrastructure.Outbox.OutboxDispatcher[0]
      Outbox message 7eb73e64-8d0e-432f-9ebf-132805b30b62 attempt 3 failed; retrying in 40ms
      System.InvalidOperationException: simulated bus failure
         at Contoso.Payments.Infrastructure.Outbox.OutboxDispatcher.DispatchOnceAsync(CancellationToken ct) in C:\Users\rukwaropaul\Downloads\DEV\Projects\01-enterprise-order-payments-platform\src\Contoso.Payments.Infrastructure\Outbox\OutboxDispatcher.cs:line 98
fail: Contoso.Payments.Infrastructure.Outbox.OutboxDispatcher[0]
      Outbox message 7eb73e64-8d0e-432f-9ebf-132805b30b62 dead-lettered after 3 attempts
  Passed Contoso.Payments.IntegrationTests.Endpoints.OutboxDispatcherTests.Failing_message_is_dead_lettered_after_MaxAttempts [2 s]
  Passed Contoso.Payments.IntegrationTests.Endpoints.AuthAndProblemDetailsTests.Correlation_id_echoed_back [2 s]
  Passed Contoso.Payments.IntegrationTests.Endpoints.OutboxDispatcherTests.Dispatcher_delivers_pending_messages [17 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.AuthAndProblemDetailsTests.Invalid_order_currency_returns_ProblemDetails [107 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.AuthAndProblemDetailsTests.Health_endpoints_report_healthy [22 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.AuthAndProblemDetailsTests.Unauthenticated_POST_orders_returns_401 [17 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.ReconciliationTests.Status_mismatch_flag_produces_row [2 s]
  Passed Contoso.Payments.IntegrationTests.Endpoints.AuthAndProblemDetailsTests.Wrong_scope_returns_403 [64 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.ReconciliationTests.Amount_mismatch_flag_produces_mismatch_row [45 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.AuthAndProblemDetailsTests.Pagination_defaults_are_applied [17 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.ReconciliationTests.Duplicate_flag_produces_row [75 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.ReconciliationTests.Clean_file_produces_all_matched [34 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.ReconciliationTests.Missing_internally_flag_produces_row [9 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.ReconciliationTests.Missing_in_provider_flag_produces_row [23 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.OutboxTransactionTests.Placing_an_order_writes_an_outbox_row_atomically [2 s]
  Passed Contoso.Payments.IntegrationTests.Endpoints.OrderIdempotencyTests.Replay_returns_Idempotent_Replay_header [2 s]
  Passed Contoso.Payments.IntegrationTests.Endpoints.OrderIdempotencyTests.Missing_idempotency_key_returns_400 [16 ms]
warn: Contoso.Payments.Api.Middleware.IdempotencyMiddleware[0]
      Idempotency conflict for key b4e15877-666b-4f18-91cc-64433a5337e3
warn: Contoso.Payments.Application.Webhooks.WebhookService[0]
      Webhook signature invalid: signature mismatch
  Passed Contoso.Payments.IntegrationTests.Endpoints.OrderIdempotencyTests.Same_key_same_body_returns_same_response [34 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.OrderIdempotencyTests.Same_key_different_body_returns_409 [20 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.PaymentFlowTests.Authorize_same_key_twice_only_charges_once [2 s]
  Passed Contoso.Payments.IntegrationTests.Endpoints.WebhookTests.Valid_webhook_captures_and_marks_order_paid [3 s]
  Passed Contoso.Payments.IntegrationTests.Endpoints.WebhookTests.Malformed_body_with_valid_signature_returns_400 [10 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.AsyncWebhookCaptureFlowTests.Full_async_capture_lifecycle_via_webhook [3 s]
  Passed Contoso.Payments.IntegrationTests.Endpoints.WebhookTests.Bad_signature_returns_401_and_does_not_mutate [83 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.PaymentFlowTests.Authorize_then_capture_then_partial_refund_matches_domain_math [299 ms]
warn: Contoso.Payments.Api.Middleware.IdempotencyMiddleware[0]
      Idempotency conflict for key 5f7ec04b-02fd-4c6e-afba-a11e021d6531
warn: Contoso.Payments.Infrastructure.Payments.ResilientPaymentProvider[0]
      Payment provider retry #0
warn: Contoso.Payments.Infrastructure.Payments.ResilientPaymentProvider[0]
      Payment provider retry #1
[xUnit.net 00:00:04.00]   Finished:    Contoso.Payments.IntegrationTests
  Passed Contoso.Payments.IntegrationTests.Endpoints.WebhookTests.Replay_of_same_signature_is_ignored [39 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.PaymentFlowTests.Second_partial_refund_exceeding_remainder_is_rejected [45 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.PaymentFlowTests.Refund_exceeding_captured_amount_returns_422 [29 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.PaymentFlowTests.Authorize_same_key_different_body_returns_409 [26 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.PaymentFlowTests.Provider_timeout_leaves_order_awaiting_payment_and_retry_recovers [208 ms]
  Passed Contoso.Payments.IntegrationTests.Endpoints.ParallelReservationTests.Parallel_orders_against_limited_stock_never_oversell [3 s]

Test Run Successful.
Total tests: 31
     Passed: 31
 Total time: 4.7111 Seconds

```

## Summary

- **Build**: 0 warnings, 0 errors.
- **Unit tests**: 53 passed / 0 failed / 0 skipped.
- **Integration tests**: 31 passed / 0 failed / 0 skipped.
- **Total**: **84 tests, all passing.**

The integration suite covers all spec-required scenarios: idempotent order +
payment + refund, replay header, 409 conflict, missing key 400, provider
timeout + retry recovery, webhook happy path + bad signature + replay +
malformed body, async webhook capture end-to-end, parallel reservation
no-oversell, outbox atomic write + delivery + dead-lettering,
reconciliation each mismatch class, auth 401/403, pagination defaults,
correlation-id echo, health checks, and ProblemDetails shape.