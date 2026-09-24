using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SusuCircle.Api.Migrations
{
    /// <inheritdoc />
    public partial class CollectionAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectionAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CircleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalAccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AccountNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    BankName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BankCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectionAccounts_Circles_CircleId",
                        column: x => x.CircleId,
                        principalTable: "Circles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CollectionLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CircleId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    CycleNumber = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ContributionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayoutId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectionLedgerEntries_CollectionAccounts_CollectionAccoun~",
                        column: x => x.CollectionAccountId,
                        principalTable: "CollectionAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionAccounts_CircleId",
                table: "CollectionAccounts",
                column: "CircleId",
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionLedgerEntries_CollectionAccountId_CreatedAt",
                table: "CollectionLedgerEntries",
                columns: new[] { "CollectionAccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionLedgerEntries_Reference",
                table: "CollectionLedgerEntries",
                column: "Reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionLedgerEntries");

            migrationBuilder.DropTable(
                name: "CollectionAccounts");
        }
    }
}
