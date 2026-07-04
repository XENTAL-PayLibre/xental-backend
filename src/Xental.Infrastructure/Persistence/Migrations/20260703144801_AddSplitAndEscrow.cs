using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSplitAndEscrow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "escrow_holds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    VirtualAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountKobo = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReleaseCondition = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escrow_holds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_escrow_holds_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_escrow_holds_virtual_accounts_VirtualAccountId",
                        column: x => x.VirtualAccountId,
                        principalTable: "virtual_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "settlement_splits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    VirtualAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    BeneficiaryName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BeneficiaryAccountNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BeneficiaryBankCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ShareBps = table.Column<int>(type: "integer", nullable: false),
                    FlatKobo = table.Column<long>(type: "bigint", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlement_splits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_settlement_splits_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_escrow_holds_TenantId",
                table: "escrow_holds",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_escrow_holds_VirtualAccountId_State",
                table: "escrow_holds",
                columns: new[] { "VirtualAccountId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_settlement_splits_TenantId_VirtualAccountId",
                table: "settlement_splits",
                columns: new[] { "TenantId", "VirtualAccountId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "escrow_holds");

            migrationBuilder.DropTable(
                name: "settlement_splits");
        }
    }
}
