using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubscriptionBilling.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "coupon_redemptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CouponId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    RedeemedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupon_redemptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "coupons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Percentage = table.Column<decimal>(type: "TEXT", precision: 7, scale: 4, nullable: false),
                    FixedAmountMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                    Duration = table.Column<string>(type: "TEXT", nullable: false),
                    DurationCycles = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxRedemptions = table.Column<int>(type: "INTEGER", nullable: true),
                    RedemptionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "credit_applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    AppliedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "credit_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_notes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "credits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    RemainingMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    TaxExempt = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReverseCharge = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dunning_cases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitialFailureAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptsCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    Recovered = table.Column<bool>(type: "INTEGER", nullable: false),
                    Escalated = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PaymentMethodToken = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dunning_cases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Route = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseContentType = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseBody = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    SubtotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    CreditAppliedMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinalizedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    BillingReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "meters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Aggregation = table.Column<string>(type: "TEXT", nullable: false),
                    RoundingIncrement = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    RoundingMode = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbound_webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_webhooks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderReference = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pending_charges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Taxable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Invoiced = table.Column<bool>(type: "INTEGER", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_charges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sequences",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NextValue = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequences", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "subscription_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OldPlanVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NewPlanVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OldQuantity = table.Column<long>(type: "INTEGER", nullable: false),
                    NewQuantity = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NetAmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Behavior = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscription_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "usage_events",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: false),
                    UniqueKey = table.Column<string>(type: "TEXT", nullable: true),
                    AdjustmentOfEventId = table.Column<string>(type: "TEXT", nullable: true),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RollupPeriodStart = table.Column<long>(type: "INTEGER", nullable: false),
                    RollupPeriodEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    Disposition = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_events", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "usage_rollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    AggregateValue = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: false),
                    SumValue = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: false),
                    MaxValue = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: false),
                    LastValue = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: false),
                    LastOccurredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastEventId = table.Column<string>(type: "TEXT", nullable: true),
                    UniqueCount = table.Column<long>(type: "INTEGER", nullable: false),
                    EventCount = table.Column<long>(type: "INTEGER", nullable: false),
                    BillableUnits = table.Column<long>(type: "INTEGER", nullable: false),
                    Closed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_rollups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "usage_unique_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<long>(type: "INTEGER", nullable: false),
                    UniqueKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_unique_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_nonces",
                columns: table => new
                {
                    Nonce = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_nonces", x => x.Nonce);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    PeriodStart = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Taxable = table.Column<bool>(type: "INTEGER", nullable: false),
                    RevenueRecognizedOverPeriod = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    IntervalUnit = table.Column<string>(type: "TEXT", nullable: false),
                    IntervalCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MeterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plans_meters_MeterId",
                        column: x => x.MeterId,
                        principalTable: "meters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_plans_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    PricingJson = table.Column<string>(type: "TEXT", nullable: false),
                    TaxInclusive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plan_versions_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlanVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentPeriodStart = table.Column<long>(type: "INTEGER", nullable: false),
                    CurrentPeriodEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    AnchorOrigin = table.Column<long>(type: "INTEGER", nullable: false),
                    AnchorDay = table.Column<int>(type: "INTEGER", nullable: false),
                    AnchorIsMonthEnd = table.Column<bool>(type: "INTEGER", nullable: false),
                    TrialEnd = table.Column<long>(type: "INTEGER", nullable: true),
                    TrialEndBehavior = table.Column<string>(type: "TEXT", nullable: false),
                    CancelAtPeriodEnd = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAccessSuspended = table.Column<bool>(type: "INTEGER", nullable: false),
                    StateBeforePause = table.Column<string>(type: "TEXT", nullable: true),
                    CouponId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CouponApplications = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviouslyActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_subscriptions_coupons_CouponId",
                        column: x => x.CouponId,
                        principalTable: "coupons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subscriptions_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subscriptions_plan_versions_PlanVersionId",
                        column: x => x.PlanVersionId,
                        principalTable: "plan_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_ResourceType_ResourceId_OccurredAt",
                table: "audit_records",
                columns: new[] { "ResourceType", "ResourceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_coupon_redemptions_CouponId_InvoiceId",
                table: "coupon_redemptions",
                columns: new[] { "CouponId", "InvoiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_coupons_Code",
                table: "coupons",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credit_applications_CreditId_InvoiceId",
                table: "credit_applications",
                columns: new[] { "CreditId", "InvoiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_Number",
                table: "credit_notes",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credits_CustomerId_CreatedAt",
                table: "credits",
                columns: new[] { "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_Name",
                table: "customers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_dunning_cases_InvoiceId",
                table: "dunning_cases",
                column: "InvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_ExpiresAt",
                table: "idempotency_records",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_Key_Route",
                table: "idempotency_records",
                columns: new[] { "Key", "Route" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_InvoiceId",
                table: "invoice_lines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_Number",
                table: "invoices",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_SubscriptionId_PeriodStart_PeriodEnd_BillingReason",
                table: "invoices",
                columns: new[] { "SubscriptionId", "PeriodStart", "PeriodEnd", "BillingReason" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meters_Name",
                table: "meters",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbound_webhooks_Status_NextAttemptAt",
                table: "outbound_webhooks",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_InvoiceId_AttemptNumber",
                table: "payment_attempts",
                columns: new[] { "InvoiceId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_charges_SubscriptionId_Invoiced",
                table: "pending_charges",
                columns: new[] { "SubscriptionId", "Invoiced" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_versions_PlanId_EffectiveFrom",
                table: "plan_versions",
                columns: new[] { "PlanId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plan_versions_PlanId_Version",
                table: "plan_versions",
                columns: new[] { "PlanId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plans_MeterId",
                table: "plans",
                column: "MeterId");

            migrationBuilder.CreateIndex(
                name: "IX_plans_ProductId_Name",
                table: "plans",
                columns: new[] { "ProductId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_products_Name",
                table: "products",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscription_changes_SubscriptionId_ChangedAt",
                table: "subscription_changes",
                columns: new[] { "SubscriptionId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_CouponId",
                table: "subscriptions",
                column: "CouponId");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_CurrentPeriodEnd",
                table: "subscriptions",
                column: "CurrentPeriodEnd");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_CustomerId_State",
                table: "subscriptions",
                columns: new[] { "CustomerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_PlanVersionId",
                table: "subscriptions",
                column: "PlanVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_usage_events_AdjustmentOfEventId",
                table: "usage_events",
                column: "AdjustmentOfEventId");

            migrationBuilder.CreateIndex(
                name: "IX_usage_events_SubscriptionId_MeterId_OccurredAt",
                table: "usage_events",
                columns: new[] { "SubscriptionId", "MeterId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_usage_rollups_SubscriptionId_MeterId_PeriodStart_PeriodEnd",
                table: "usage_rollups",
                columns: new[] { "SubscriptionId", "MeterId", "PeriodStart", "PeriodEnd" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usage_unique_keys_SubscriptionId_MeterId_PeriodStart_UniqueKey",
                table: "usage_unique_keys",
                columns: new[] { "SubscriptionId", "MeterId", "PeriodStart", "UniqueKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_nonces_ExpiresAt",
                table: "webhook_nonces",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_records");

            migrationBuilder.DropTable(
                name: "coupon_redemptions");

            migrationBuilder.DropTable(
                name: "credit_applications");

            migrationBuilder.DropTable(
                name: "credit_notes");

            migrationBuilder.DropTable(
                name: "credits");

            migrationBuilder.DropTable(
                name: "dunning_cases");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "invoice_lines");

            migrationBuilder.DropTable(
                name: "outbound_webhooks");

            migrationBuilder.DropTable(
                name: "payment_attempts");

            migrationBuilder.DropTable(
                name: "pending_charges");

            migrationBuilder.DropTable(
                name: "sequences");

            migrationBuilder.DropTable(
                name: "subscription_changes");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "usage_events");

            migrationBuilder.DropTable(
                name: "usage_rollups");

            migrationBuilder.DropTable(
                name: "usage_unique_keys");

            migrationBuilder.DropTable(
                name: "webhook_nonces");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "coupons");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "plan_versions");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "meters");

            migrationBuilder.DropTable(
                name: "products");
        }
    }
}
