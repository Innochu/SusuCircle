using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SusuCircle.Api.Migrations
{
    /// <inheritdoc />
    public partial class CollectionAccountPlaceholder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPlaceholder",
                table: "CollectionAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProvisioningError",
                table: "CollectionAccounts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPlaceholder",
                table: "CollectionAccounts");

            migrationBuilder.DropColumn(
                name: "ProvisioningError",
                table: "CollectionAccounts");
        }
    }
}
