using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SettledAtUtc",
                table: "virtual_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "settlement_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SettlementAccountNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SettlementBankCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SettlementAccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AutoSettle = table.Column<bool>(type: "boolean", nullable: false),
                    MinPayoutKobo = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlement_configs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_settlement_configs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_settlement_configs_TenantId",
                table: "settlement_configs",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "settlement_configs");

            migrationBuilder.DropColumn(
                name: "SettledAtUtc",
                table: "virtual_accounts");
        }
    }
}
