using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260702160000_AddCommerceBusinessScope")]
    public partial class AddCommerceBusinessScope : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommerceBusinesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BusinessType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    OwnerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    PrimaryDomain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinesses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinesses_IsActive",
                table: "CommerceBusinesses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinesses_Key",
                table: "CommerceBusinesses",
                column: "Key",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommerceBusinesses");
        }
    }
}
