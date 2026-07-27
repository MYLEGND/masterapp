using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialPostMediaAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SocialPostMediaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    MediaKind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ThumbnailStorageKey = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    MimeType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    AspectRatio = table.Column<decimal>(type: "decimal(12,6)", precision: 12, scale: 6, nullable: true),
                    DurationSeconds = table.Column<decimal>(type: "decimal(12,3)", precision: 12, scale: 3, nullable: true),
                    ProcessingState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AccessibilityText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostMediaAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostMediaAssets_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMediaAssets_ProcessingState_CreatedUtc",
                table: "SocialPostMediaAssets",
                columns: new[] { "ProcessingState", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMediaAssets_SocialPostId_DisplayOrder",
                table: "SocialPostMediaAssets",
                columns: new[] { "SocialPostId", "DisplayOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMediaAssets_StorageKey",
                table: "SocialPostMediaAssets",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SocialPostMediaAssets");
        }
    }
}
