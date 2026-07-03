using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeveloperKyc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "developer_kyc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                    Country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IdType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    IdNumberEncrypted = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IdNumberHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BankName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BankCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BankAccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BankAccountNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PortfolioUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProjectDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_developer_kyc", x => x.Id);
                    table.ForeignKey(
                        name: "FK_developer_kyc_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "verification_checks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_checks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_verification_checks_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_developer_kyc_IdNumberHash",
                table: "developer_kyc",
                column: "IdNumberHash");

            migrationBuilder.CreateIndex(
                name: "IX_developer_kyc_TenantId",
                table: "developer_kyc",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_verification_checks_TenantId_Kind",
                table: "verification_checks",
                columns: new[] { "TenantId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "developer_kyc");

            migrationBuilder.DropTable(
                name: "verification_checks");
        }
    }
}
