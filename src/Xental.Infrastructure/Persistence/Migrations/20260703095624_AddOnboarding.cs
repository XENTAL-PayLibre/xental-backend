using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "onboarding_applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DeveloperKycStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BusinessKybStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onboarding_applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_onboarding_applications_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_applications_TenantId",
                table: "onboarding_applications",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onboarding_applications");
        }
    }
}
