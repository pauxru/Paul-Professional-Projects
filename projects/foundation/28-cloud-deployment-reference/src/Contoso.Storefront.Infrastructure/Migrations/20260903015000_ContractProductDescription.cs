using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Contoso.Storefront.Infrastructure.Migrations;

[DbContext(typeof(StorefrontDbContext))]
[Migration("20260903015000_ContractProductDescription")]
public sealed class ContractProductDescription : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE products_contract (
                    id TEXT NOT NULL CONSTRAINT pk_products PRIMARY KEY,
                    sku TEXT NOT NULL,
                    name TEXT NOT NULL,
                    description_v2 TEXT NULL,
                    price_amount TEXT NOT NULL,
                    currency TEXT NOT NULL,
                    is_active INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    version INTEGER NOT NULL
                );
                INSERT INTO products_contract
                    (id, sku, name, description_v2, price_amount, currency, is_active, created_at, version)
                SELECT
                    id, sku, name, description_v2, price_amount, currency, is_active, created_at, version
                FROM products;
                DROP TABLE products;
                ALTER TABLE products_contract RENAME TO products;
                CREATE UNIQUE INDEX ux_products_sku ON products (sku);
                """);
        }
        else
        {
            migrationBuilder.DropColumn(
                name: "description",
                table: "products");
        }

        migrationBuilder.Sql(
            "UPDATE deployment_migration_states SET phase = 'contracted-v2-only' WHERE name = 'product-description';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "description",
            table: "products",
            maxLength: 2_000,
            nullable: false,
            defaultValue: string.Empty);
        migrationBuilder.Sql("UPDATE products SET description = description_v2;");
        migrationBuilder.Sql(
            "UPDATE deployment_migration_states SET phase = 'read-v2-dual-write' WHERE name = 'product-description';");
    }
}
