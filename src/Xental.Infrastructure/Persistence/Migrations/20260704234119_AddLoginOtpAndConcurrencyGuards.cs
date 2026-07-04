using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginOtpAndConcurrencyGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_billing_schedules_VirtualAccountId",
                table: "billing_schedules");

            migrationBuilder.AddColumn<int>(
                name: "ConcurrencyToken",
                table: "virtual_accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConcurrencyToken",
                table: "billing_schedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "login_otps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    CodeHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Consumed = table.Column<bool>(type: "boolean", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_otps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_login_otps_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_billing_schedules_active_virtual_account",
                table: "billing_schedules",
                column: "VirtualAccountId",
                unique: true,
                filter: "\"Status\" <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "IX_login_otps_TenantId_TeamMemberId_Consumed",
                table: "login_otps",
                columns: new[] { "TenantId", "TeamMemberId", "Consumed" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_otps");

            migrationBuilder.DropIndex(
                name: "UX_billing_schedules_active_virtual_account",
                table: "billing_schedules");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "virtual_accounts");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "billing_schedules");

            migrationBuilder.CreateIndex(
                name: "IX_billing_schedules_VirtualAccountId",
                table: "billing_schedules",
                column: "VirtualAccountId");
        }
    }
}
