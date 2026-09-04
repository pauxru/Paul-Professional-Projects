using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Contoso.Storefront.Infrastructure.Migrations;

[DbContext(typeof(StorefrontDbContext))]
[Migration("20260903010000_InitialSchema")]
public sealed class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "deployment_migration_states",
            columns: table => new
            {
                name = table.Column<string>(maxLength: 100, nullable: false),
                phase = table.Column<string>(maxLength: 100, nullable: false)
            },
            constraints: table => table.PrimaryKey("pk_deployment_migration_states", x => x.name));

        migrationBuilder.CreateTable(
            name: "orders",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false),
                customer_reference = table.Column<string>(maxLength: 100, nullable: false),
                idempotency_key = table.Column<string>(maxLength: 100, nullable: false),
                created_at = table.Column<DateTimeOffset>(nullable: false),
                status = table.Column<string>(maxLength: 32, nullable: false),
                version = table.Column<int>(nullable: false)
            },
            constraints: table => table.PrimaryKey("pk_orders", x => x.id));

        migrationBuilder.CreateTable(
            name: "outbox_messages",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false),
                type = table.Column<string>(maxLength: 200, nullable: false),
                payload = table.Column<string>(nullable: false),
                occurred_at = table.Column<DateTimeOffset>(nullable: false),
                processed_at = table.Column<DateTimeOffset>(nullable: true),
                delivery_attempts = table.Column<int>(nullable: false),
                last_error = table.Column<string>(maxLength: 2_000, nullable: true)
            },
            constraints: table => table.PrimaryKey("pk_outbox_messages", x => x.id));

        migrationBuilder.CreateTable(
            name: "products",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false),
                sku = table.Column<string>(maxLength: 64, nullable: false),
                name = table.Column<string>(maxLength: 200, nullable: false),
                description = table.Column<string>(maxLength: 2_000, nullable: false),
                price_amount = table.Column<decimal>(precision: 18, scale: 2, nullable: false),
                currency = table.Column<string>(maxLength: 3, nullable: false),
                is_active = table.Column<bool>(nullable: false),
                created_at = table.Column<DateTimeOffset>(nullable: false),
                version = table.Column<int>(nullable: false)
            },
            constraints: table => table.PrimaryKey("pk_products", x => x.id));

        migrationBuilder.CreateTable(
            name: "worker_checkpoints",
            columns: table => new
            {
                worker_name = table.Column<string>(maxLength: 100, nullable: false),
                last_message_id = table.Column<Guid>(nullable: false),
                updated_at = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("pk_worker_checkpoints", x => x.worker_name));

        migrationBuilder.CreateTable(
            name: "order_items",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false),
                order_id = table.Column<Guid>(nullable: false),
                product_id = table.Column<Guid>(nullable: false),
                product_name = table.Column<string>(maxLength: 200, nullable: false),
                quantity = table.Column<int>(nullable: false),
                unit_price_amount = table.Column<decimal>(precision: 18, scale: 2, nullable: false),
                currency = table.Column<string>(maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_order_items", x => x.id);
                table.ForeignKey(
                    name: "fk_order_items_orders_order_id",
                    column: x => x.order_id,
                    principalTable: "orders",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_order_items_order_id",
            table: "order_items",
            column: "order_id");
        migrationBuilder.CreateIndex(
            name: "ix_order_items_product_id",
            table: "order_items",
            column: "product_id");
        migrationBuilder.CreateIndex(
            name: "ux_orders_idempotency_key",
            table: "orders",
            column: "idempotency_key",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ix_outbox_pending",
            table: "outbox_messages",
            columns: new[] { "processed_at", "occurred_at" });
        migrationBuilder.CreateIndex(
            name: "ux_products_sku",
            table: "products",
            column: "sku",
            unique: true);

        migrationBuilder.Sql(
            "INSERT INTO deployment_migration_states (name, phase) VALUES ('product-description', 'legacy-read-write');");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("deployment_migration_states");
        migrationBuilder.DropTable("order_items");
        migrationBuilder.DropTable("outbox_messages");
        migrationBuilder.DropTable("products");
        migrationBuilder.DropTable("worker_checkpoints");
        migrationBuilder.DropTable("orders");
    }
}
