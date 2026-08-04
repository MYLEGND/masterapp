using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountLifecycleAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountLifecycleRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PausedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletionRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountLifecycleRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountLifecycleRecords_ProfileId_ParticipantType",
                table: "AccountLifecycleRecords",
                columns: new[] { "ProfileId", "ParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountLifecycleRecords_State_UpdatedUtc",
                table: "AccountLifecycleRecords",
                columns: new[] { "State", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountLifecycleRecords_UserId_ParticipantType",
                table: "AccountLifecycleRecords",
                columns: new[] { "UserId", "ParticipantType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountLifecycleRecords");
        }
    }
}
