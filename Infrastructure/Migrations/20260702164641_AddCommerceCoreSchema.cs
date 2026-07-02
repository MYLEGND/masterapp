using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCommerceCoreSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommerceBusinessSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShippingFeeCents = table.Column<int>(type: "int", nullable: false),
                    TaxPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    GlobalDiscountCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    GlobalDiscountType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    GlobalDiscountAmount = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    GlobalDiscountIsActive = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinessSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceBusinessSettings_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderNumber = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaidUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ShippedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FulfilledUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PaymentStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FulfillmentStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ReturnStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CheckoutAttemptId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    IsPaymentProcessing = table.Column<bool>(type: "bit", nullable: false),
                    PaymentProcessingStartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SquarePaymentId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    SquareError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TrackingCarrier = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    TrackingNumber = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    AdminNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    AddressLine1 = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    AddressLine2 = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    City = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    State = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PostalCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    UserAgent = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RequestIp = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    SubtotalCents = table.Column<int>(type: "int", nullable: false),
                    DiscountCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    DiscountLabel = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    DiscountCents = table.Column<int>(type: "int", nullable: false),
                    RefundedCents = table.Column<int>(type: "int", nullable: false),
                    ShippingCents = table.Column<int>(type: "int", nullable: false),
                    TaxCents = table.Column<int>(type: "int", nullable: false),
                    TotalCents = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceOrders_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceProducts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalProductKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PriceLabel = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Badge = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PriceCents = table.Column<int>(type: "int", nullable: false),
                    CompareAtPriceCents = table.Column<int>(type: "int", nullable: false),
                    IsFeatured = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceProducts_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductExternalKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    ProductSlug = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Size = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPriceCents = table.Column<int>(type: "int", nullable: false),
                    CompareAtPriceCents = table.Column<int>(type: "int", nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceOrderLines_CommerceOrders_CommerceOrderId",
                        column: x => x.CommerceOrderId,
                        principalTable: "CommerceOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceProductDiscounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalDiscountKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DiscountType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceProductDiscounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceProductDiscounts_CommerceProducts_CommerceProductId",
                        column: x => x.CommerceProductId,
                        principalTable: "CommerceProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceProductImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalImageKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    AltText = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    ObjectFit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ObjectPositionX = table.Column<int>(type: "int", nullable: false),
                    ObjectPositionY = table.Column<int>(type: "int", nullable: false),
                    Zoom = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceProductImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceProductImages_CommerceProducts_CommerceProductId",
                        column: x => x.CommerceProductId,
                        principalTable: "CommerceProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceProductInventoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalInventoryKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Size = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    StockQuantity = table.Column<int>(type: "int", nullable: false),
                    LowStockThreshold = table.Column<int>(type: "int", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceProductInventoryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceProductInventoryItems_CommerceProducts_CommerceProductId",
                        column: x => x.CommerceProductId,
                        principalTable: "CommerceProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessSettings_CommerceBusinessId",
                table: "CommerceBusinessSettings",
                column: "CommerceBusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrderLines_CommerceOrderId",
                table: "CommerceOrderLines",
                column: "CommerceOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_CheckoutAttemptId",
                table: "CommerceOrders",
                column: "CheckoutAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_CommerceBusinessId_CreatedUtc",
                table: "CommerceOrders",
                columns: new[] { "CommerceBusinessId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_CommerceBusinessId_OrderNumber",
                table: "CommerceOrders",
                columns: new[] { "CommerceBusinessId", "OrderNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_CommerceBusinessId_PaymentStatus_FulfillmentStatus",
                table: "CommerceOrders",
                columns: new[] { "CommerceBusinessId", "PaymentStatus", "FulfillmentStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductDiscounts_CommerceProductId_Code",
                table: "CommerceProductDiscounts",
                columns: new[] { "CommerceProductId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductDiscounts_CommerceProductId_ExternalDiscountKey",
                table: "CommerceProductDiscounts",
                columns: new[] { "CommerceProductId", "ExternalDiscountKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductImages_CommerceProductId_DisplayOrder",
                table: "CommerceProductImages",
                columns: new[] { "CommerceProductId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductImages_CommerceProductId_ExternalImageKey",
                table: "CommerceProductImages",
                columns: new[] { "CommerceProductId", "ExternalImageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductInventoryItems_CommerceProductId_Size",
                table: "CommerceProductInventoryItems",
                columns: new[] { "CommerceProductId", "Size" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProducts_CommerceBusinessId_ExternalProductKey",
                table: "CommerceProducts",
                columns: new[] { "CommerceBusinessId", "ExternalProductKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProducts_CommerceBusinessId_IsActive_DisplayOrder",
                table: "CommerceProducts",
                columns: new[] { "CommerceBusinessId", "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProducts_CommerceBusinessId_Slug",
                table: "CommerceProducts",
                columns: new[] { "CommerceBusinessId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommerceBusinessSettings");

            migrationBuilder.DropTable(
                name: "CommerceOrderLines");

            migrationBuilder.DropTable(
                name: "CommerceProductDiscounts");

            migrationBuilder.DropTable(
                name: "CommerceProductImages");

            migrationBuilder.DropTable(
                name: "CommerceProductInventoryItems");

            migrationBuilder.DropTable(
                name: "CommerceOrders");

            migrationBuilder.DropTable(
                name: "CommerceProducts");
        }
    }
}
