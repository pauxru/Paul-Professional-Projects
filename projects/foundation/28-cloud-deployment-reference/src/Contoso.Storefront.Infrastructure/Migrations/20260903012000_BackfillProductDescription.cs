using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Contoso.Storefront.Infrastructure.Migrations;

[DbContext(typeof(StorefrontDbContext))]
[Migration("20260903012000_BackfillProductDescription")]
public sealed class BackfillProductDescription : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE products SET description_v2 = description WHERE description_v2 IS NULL;");
        migrationBuilder.Sql(
            "UPDATE deployment_migration_states SET phase = 'backfilled' WHERE name = 'product-description';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE deployment_migration_states SET phase = 'expanded-nullable-column' WHERE name = 'product-description';");
    }
}
