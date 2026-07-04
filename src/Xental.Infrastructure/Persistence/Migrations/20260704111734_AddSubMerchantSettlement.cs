using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubMerchantSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SettledUpToKobo",
                table: "virtual_accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "SubMerchantId",
                table: "virtual_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlatformFeeBps",
                table: "sub_merchants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SettlementAccountName",
                table: "sub_merchants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementAccountNumber",
                table: "sub_merchants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementBankCode",
                table: "sub_merchants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementBankName",
                table: "sub_merchants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_virtual_accounts_SubMerchantId",
                table: "virtual_accounts",
                column: "SubMerchantId");

            migrationBuilder.AddForeignKey(
                name: "FK_virtual_accounts_sub_merchants_SubMerchantId",
                table: "virtual_accounts",
                column: "SubMerchantId",
                principalTable: "sub_merchants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_virtual_accounts_sub_merchants_SubMerchantId",
                table: "virtual_accounts");

            migrationBuilder.DropIndex(
                name: "IX_virtual_accounts_SubMerchantId",
                table: "virtual_accounts");

            migrationBuilder.DropColumn(
                name: "SettledUpToKobo",
                table: "virtual_accounts");

            migrationBuilder.DropColumn(
                name: "SubMerchantId",
                table: "virtual_accounts");

            migrationBuilder.DropColumn(
                name: "PlatformFeeBps",
                table: "sub_merchants");

            migrationBuilder.DropColumn(
                name: "SettlementAccountName",
                table: "sub_merchants");

            migrationBuilder.DropColumn(
                name: "SettlementAccountNumber",
                table: "sub_merchants");

            migrationBuilder.DropColumn(
                name: "SettlementBankCode",
                table: "sub_merchants");

            migrationBuilder.DropColumn(
                name: "SettlementBankName",
                table: "sub_merchants");
        }
    }
}
