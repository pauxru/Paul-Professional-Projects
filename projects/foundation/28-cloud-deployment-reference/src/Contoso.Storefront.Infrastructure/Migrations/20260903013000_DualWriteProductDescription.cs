using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Contoso.Storefront.Infrastructure.Migrations;

[DbContext(typeof(StorefrontDbContext))]
[Migration("20260903013000_DualWriteProductDescription")]
public sealed class DualWriteProductDescription : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE deployment_migration_states SET phase = 'dual-write' WHERE name = 'product-description';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE deployment_migration_states SET phase = 'backfilled' WHERE name = 'product-description';");
    }
}
