using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260808021000_AddFounderSubscriptionTrials")]
public partial class AddFounderSubscriptionTrials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "FreeTrialDays",
            table: "ClientSubscriptionOffers",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "TrialEndsUtc",
            table: "ClientSubscriptions",
            type: "datetime2",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FreeTrialDays",
            table: "ClientSubscriptionOffers");

        migrationBuilder.DropColumn(
            name: "TrialEndsUtc",
            table: "ClientSubscriptions");
    }
}
