using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260331000000_CommitmentsMvp")]
    public partial class CommitmentsMvp : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Commitments",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    RelatedEntityType = table.Column<string>(maxLength: 40, nullable: false),
                    RelatedEntityId = table.Column<string>(maxLength: 180, nullable: false),
                    PromisedByType = table.Column<string>(maxLength: 40, nullable: false),
                    PromisedById = table.Column<string>(maxLength: 180, nullable: false),
                    PromisedToType = table.Column<string>(maxLength: 40, nullable: false),
                    PromisedToId = table.Column<string>(maxLength: 180, nullable: false),
                    PromiseText = table.Column<string>(maxLength: 500, nullable: false),
                    DueDateUtc = table.Column<DateTimeOffset>(nullable: false),
                    Status = table.Column<string>(maxLength: 32, nullable: false),
                    LinkedActionId = table.Column<Guid>(nullable: true),
                    CreatedBy = table.Column<string>(maxLength: 180, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(nullable: false),
                    FulfilledAtUtc = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commitments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Commitments_DueDateUtc_Status",
                table: "Commitments",
                columns: new[] { "DueDateUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Commitments_PromisedById_Status",
                table: "Commitments",
                columns: new[] { "PromisedById", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Commitments_RelatedEntityType_RelatedEntityId",
                table: "Commitments",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Commitments");
        }
    }
}
