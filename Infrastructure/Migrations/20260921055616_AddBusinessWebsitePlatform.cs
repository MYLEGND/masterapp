using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessWebsitePlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicFactsJson",
                table: "CommerceBusinessStorefrontSettings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientProfileId",
                table: "CommerceBusinessMembers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WebsiteContentState",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SiteKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DraftJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImportReportJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScheduledPublishUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ScheduledRevision = table.Column<long>(type: "bigint", nullable: true),
                    ScheduledActorJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScheduleError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebsiteContentState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebsiteDomainBinding",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hostname = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    ProviderHostnameId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CertificateStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    VerificationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastCheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebsiteDomainBinding", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebsiteDomainBinding_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebsiteMediaAsset",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebsiteMediaAsset", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebsiteContentVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CompiledPagesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImportReportJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebsiteContentVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebsiteContentVersion_WebsiteContentState_StateId",
                        column: x => x.StateId,
                        principalTable: "WebsiteContentState",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommerceWebsiteInquiry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    SourcePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceWebsiteInquiry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceWebsiteInquiry_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommerceWebsiteInquiry_WebsiteContentVersion_PublishedVersionId",
                        column: x => x.PublishedVersionId,
                        principalTable: "WebsiteContentVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessMembers_ClientProfileId_CommerceBusinessId",
                table: "CommerceBusinessMembers",
                columns: new[] { "ClientProfileId", "CommerceBusinessId" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceWebsiteInquiry_CommerceBusinessId_CreatedUtc",
                table: "CommerceWebsiteInquiry",
                columns: new[] { "CommerceBusinessId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceWebsiteInquiry_CommerceBusinessId_SubmissionId",
                table: "CommerceWebsiteInquiry",
                columns: new[] { "CommerceBusinessId", "SubmissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceWebsiteInquiry_PublishedVersionId",
                table: "CommerceWebsiteInquiry",
                column: "PublishedVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteContentState_OwnerKey_SiteKey",
                table: "WebsiteContentState",
                columns: new[] { "OwnerKey", "SiteKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteContentVersion_StateId_Revision",
                table: "WebsiteContentVersion",
                columns: new[] { "StateId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteDomainBinding_CommerceBusinessId",
                table: "WebsiteDomainBinding",
                column: "CommerceBusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteDomainBinding_Hostname",
                table: "WebsiteDomainBinding",
                column: "Hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteMediaAsset_OwnerKey_Sha256",
                table: "WebsiteMediaAsset",
                columns: new[] { "OwnerKey", "Sha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommerceWebsiteInquiry");

            migrationBuilder.DropTable(
                name: "WebsiteDomainBinding");

            migrationBuilder.DropTable(
                name: "WebsiteMediaAsset");

            migrationBuilder.DropTable(
                name: "WebsiteContentVersion");

            migrationBuilder.DropTable(
                name: "WebsiteContentState");

            migrationBuilder.DropIndex(
                name: "IX_CommerceBusinessMembers_ClientProfileId_CommerceBusinessId",
                table: "CommerceBusinessMembers");

            migrationBuilder.DropColumn(
                name: "PublicFactsJson",
                table: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropColumn(
                name: "ClientProfileId",
                table: "CommerceBusinessMembers");
        }
    }
}
