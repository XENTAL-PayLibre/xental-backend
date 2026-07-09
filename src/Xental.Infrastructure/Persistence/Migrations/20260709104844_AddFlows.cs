using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flow_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AccountRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TransactionRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_flow_runs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "flows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    MinAmountKobo = table.Column<long>(type: "bigint", nullable: true),
                    MinRiskScore = table.Column<int>(type: "integer", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_flows_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "flow_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_flow_actions_flows_FlowId",
                        column: x => x.FlowId,
                        principalTable: "flows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flow_actions_FlowId",
                table: "flow_actions",
                column: "FlowId");

            migrationBuilder.CreateIndex(
                name: "IX_flow_runs_TenantId_CreatedAtUtc",
                table: "flow_runs",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_flows_TenantId_Enabled",
                table: "flows",
                columns: new[] { "TenantId", "Enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_actions");

            migrationBuilder.DropTable(
                name: "flow_runs");

            migrationBuilder.DropTable(
                name: "flows");
        }
    }
}
