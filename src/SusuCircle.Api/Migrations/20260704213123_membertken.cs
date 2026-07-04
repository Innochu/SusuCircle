using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SusuCircle.Api.Migrations
{
    /// <inheritdoc />
    public partial class membertken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefreshToken",
                table: "Members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefreshTokenExpiry",
                table: "Members",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefreshToken",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "RefreshTokenExpiry",
                table: "Members");
        }
    }
}
