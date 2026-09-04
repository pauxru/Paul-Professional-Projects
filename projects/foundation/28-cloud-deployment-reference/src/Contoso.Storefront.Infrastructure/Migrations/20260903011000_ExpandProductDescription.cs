using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Contoso.Storefront.Infrastructure.Migrations;

[DbContext(typeof(StorefrontDbContext))]
[Migration("20260903011000_ExpandProductDescription")]
public sealed class ExpandProductDescription : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "description_v2",
            table: "products",
            maxLength: 2_000,
            nullable: true);
        migrationBuilder.Sql(
            "UPDATE deployment_migration_states SET phase = 'expanded-nullable-column' WHERE name = 'product-description';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("description_v2", "products");
        migrationBuilder.Sql(
            "UPDATE deployment_migration_states SET phase = 'legacy-read-write' WHERE name = 'product-description';");
    }
}
