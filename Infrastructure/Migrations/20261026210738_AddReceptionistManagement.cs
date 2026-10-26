using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReceptionistManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReceptionistAcceptanceWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarketingConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IngressKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProviderProjectKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RestrictedRouteEvidenceSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedByActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReviewedByActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false, collation: "Latin1_General_100_BIN2"),
                    StartsUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistAcceptanceWindows", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistAcceptanceWindow_Lifetime", "[StartsUtc] < [ExpiresUtc]");
                    table.ForeignKey(
                        name: "FK_ReceptionistAcceptanceWindows_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId",
                        columns: x => new { x.MarketingConnectionId, x.ConfigurationVersionId },
                        principalTable: "ReceptionistConfigurationVersions",
                        principalColumns: new[] { "MarketingConnectionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistCredentialVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarketingConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProviderProjectKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Purpose = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProtectedValue = table.Column<string>(type: "nvarchar(max)", maxLength: 32768, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ValueSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedByActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistCredentialVersions", x => x.Id);
                    table.UniqueConstraint("AK_ReceptionistCredentialVersions_MarketingConnectionId_Id", x => new { x.MarketingConnectionId, x.Id });
                    table.ForeignKey(
                        name: "FK_ReceptionistCredentialVersions_MarketingConnections_MarketingConnectionId",
                        column: x => x.MarketingConnectionId,
                        principalTable: "MarketingConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistOperationPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarketingConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ApiCredentialVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WebhookCredentialVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReviewedByActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ReviewedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewedPayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistOperationPackages", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistOperationPackage_Lifetime", "[CreatedUtc] < [ExpiresUtc]");
                    table.CheckConstraint("CK_ReceptionistOperationPackage_Review", "[ReviewState] IN ('pending','accepted','rejected') AND ([ReviewState] = 'pending' OR ([ReviewedByActorId] IS NOT NULL AND [ReviewedUtc] IS NOT NULL AND [ReviewedPayloadSha256] IS NOT NULL AND [ReviewedPayloadSha256] = [PayloadSha256]))");
                    table.ForeignKey(
                        name: "FK_ReceptionistOperationPackages_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId",
                        columns: x => new { x.MarketingConnectionId, x.ConfigurationVersionId },
                        principalTable: "ReceptionistConfigurationVersions",
                        principalColumns: new[] { "MarketingConnectionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistOperationPackages_ReceptionistCredentialVersions_MarketingConnectionId_ApiCredentialVersionId",
                        columns: x => new { x.MarketingConnectionId, x.ApiCredentialVersionId },
                        principalTable: "ReceptionistCredentialVersions",
                        principalColumns: new[] { "MarketingConnectionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistOperationPackages_ReceptionistCredentialVersions_MarketingConnectionId_WebhookCredentialVersionId",
                        columns: x => new { x.MarketingConnectionId, x.WebhookCredentialVersionId },
                        principalTable: "ReceptionistCredentialVersions",
                        principalColumns: new[] { "MarketingConnectionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAcceptanceWindows_MarketingConnectionId_ConfigurationVersionId_ExpiresUtc",
                table: "ReceptionistAcceptanceWindows",
                columns: new[] { "MarketingConnectionId", "ConfigurationVersionId", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistOperationPackages_MarketingConnectionId_ApiCredentialVersionId",
                table: "ReceptionistOperationPackages",
                columns: new[] { "MarketingConnectionId", "ApiCredentialVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistOperationPackages_MarketingConnectionId_ConfigurationVersionId",
                table: "ReceptionistOperationPackages",
                columns: new[] { "MarketingConnectionId", "ConfigurationVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistOperationPackages_MarketingConnectionId_CreatedUtc",
                table: "ReceptionistOperationPackages",
                columns: new[] { "MarketingConnectionId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistOperationPackages_MarketingConnectionId_WebhookCredentialVersionId",
                table: "ReceptionistOperationPackages",
                columns: new[] { "MarketingConnectionId", "WebhookCredentialVersionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceptionistAcceptanceWindows");

            migrationBuilder.DropTable(
                name: "ReceptionistOperationPackages");

            migrationBuilder.DropTable(
                name: "ReceptionistCredentialVersions");
        }
    }
}
