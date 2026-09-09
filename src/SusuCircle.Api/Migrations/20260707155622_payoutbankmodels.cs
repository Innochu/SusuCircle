using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SusuCircle.Api.Migrations
{
    /// <inheritdoc />
    public partial class payoutbankmodels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayoutBankAccountNumber",
                table: "Members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayoutBankCode",
                table: "Members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayoutBankLabel",
                table: "Members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayoutBankName",
                table: "Members",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayoutBankAccountNumber",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "PayoutBankCode",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "PayoutBankLabel",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "PayoutBankName",
                table: "Members");
        }
    }
}


//adding member to circle fails
//  "message": "Failed to provision virtual account for chukwudi Innocent: Token issuance failed (404).",
//https://susucircle.onrender.com/api/circles/6241ef37-e139-44d3-a4b0-61e45d6d2b32/members