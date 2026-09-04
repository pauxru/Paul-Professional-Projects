using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Northstar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NorthstarDbContext))]
[Migration("202609030001_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Policyholders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Policyholders", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Policies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PolicyholderId = table.Column<Guid>(type: "TEXT", nullable: false),
                PolicyNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                DeductibleAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                LimitAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Policies", x => x.Id);
                table.ForeignKey("FK_Policies_Policyholders_PolicyholderId", x => x.PolicyholderId, "Policyholders", "Id", onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("CK_Policies_Deductible", "\"DeductibleAmount\" >= 0");
                table.CheckConstraint("CK_Policies_Limit", "\"LimitAmount\" > 0");
            });

        migrationBuilder.CreateTable(
            name: "Claims",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                Reference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ClaimedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                ReserveAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                SettlementAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                AssignedAdjuster = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                Version = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Claims", x => x.Id);
                table.ForeignKey("FK_Claims_Policies_PolicyId", x => x.PolicyId, "Policies", "Id", onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("CK_Claims_ClaimedAmount", "\"ClaimedAmount\" > 0");
                table.CheckConstraint("CK_Claims_ReserveAmount", "\"ReserveAmount\" >= 0");
                table.CheckConstraint("CK_Claims_SettlementAmount", "\"SettlementAmount\" >= 0");
            });

        migrationBuilder.CreateTable(
            name: "ClaimDocuments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ClaimId = table.Column<Guid>(type: "TEXT", nullable: false),
                OriginalName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                StorageKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                UploadedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ClaimDocuments", x => x.Id);
                table.ForeignKey("FK_ClaimDocuments_Claims_ClaimId", x => x.ClaimId, "Claims", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AuditRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Actor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Resource = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                OccurredAt = table.Column<string>(type: "TEXT", nullable: false),
                CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                SourceIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                BeforeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AfterHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AuditRecords", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_Policyholders_Email", table: "Policyholders", column: "Email", unique: true);
        migrationBuilder.CreateIndex(name: "IX_Policies_PolicyNumber", table: "Policies", column: "PolicyNumber", unique: true);
        migrationBuilder.CreateIndex(name: "IX_Policies_PolicyholderId", table: "Policies", column: "PolicyholderId");
        migrationBuilder.CreateIndex(name: "IX_Claims_PolicyId", table: "Claims", column: "PolicyId");
        migrationBuilder.CreateIndex(name: "IX_Claims_Reference", table: "Claims", column: "Reference", unique: true);
        migrationBuilder.CreateIndex(name: "IX_Claims_Status_CreatedAt", table: "Claims", columns: new[] { "Status", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_ClaimDocuments_ClaimId", table: "ClaimDocuments", column: "ClaimId");
        migrationBuilder.CreateIndex(name: "IX_AuditRecords_CorrelationId", table: "AuditRecords", column: "CorrelationId");
        migrationBuilder.CreateIndex(name: "IX_AuditRecords_Resource_OccurredAt", table: "AuditRecords", columns: new[] { "Resource", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AuditRecords");
        migrationBuilder.DropTable(name: "ClaimDocuments");
        migrationBuilder.DropTable(name: "Claims");
        migrationBuilder.DropTable(name: "Policies");
        migrationBuilder.DropTable(name: "Policyholders");
    }
}
