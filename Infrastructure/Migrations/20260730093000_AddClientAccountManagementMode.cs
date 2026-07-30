using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260730093000_AddClientAccountManagementMode")]
    public partial class AddClientAccountManagementMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountManagementMode",
                table: "ClientProfiles",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "SharedAccount");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountManagementMode",
                table: "ClientProfiles");
        }
    }
}
