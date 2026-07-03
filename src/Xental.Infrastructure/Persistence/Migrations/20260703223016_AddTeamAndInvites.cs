using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamAndInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcceptedAtUtc",
                table: "team_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InviteExpiresAtUtc",
                table: "team_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InviteTokenHash",
                table: "team_members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "team_members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamMemberId",
                table: "refresh_tokens",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptedAtUtc",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "InviteExpiresAtUtc",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "InviteTokenHash",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "TeamMemberId",
                table: "refresh_tokens");
        }
    }
}
