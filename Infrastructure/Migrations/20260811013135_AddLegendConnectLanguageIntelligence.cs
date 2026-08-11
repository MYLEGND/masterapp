using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectLanguageIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendCorpusCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    SourceLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceText = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    SourceTextHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsApproved = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ProcessingState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendCorpusCandidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendGlobalConcepts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConceptKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendGlobalConcepts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BaseLanguageCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CanonicalName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NativeName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsTranslationEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsLearningEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DatasetNamespace = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    StoragePartition = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguagePairs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: false),
                    SourceLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TranslationMemoryPartition = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    CorpusCoverage = table.Column<int>(type: "int", nullable: false),
                    QualityState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActiveModelVersion = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ProviderFallbackPolicy = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguagePairs", x => x.Id);
                });

            // Carry the existing server catalog into a durable registry.
            // Future languages are added as data; no per-language service or
            // mobile release is required.
            migrationBuilder.InsertData(
                table: "LegendLanguageDefinitions",
                columns: new[]
                {
                    "Id", "LanguageCode", "BaseLanguageCode", "CanonicalName", "NativeName",
                    "IsEnabled", "IsTranslationEnabled", "IsLearningEnabled", "DatasetNamespace",
                    "StoragePartition", "CreatedUtc", "UpdatedUtc"
                },
                values: new object[,]
                {
                    { new Guid("b42b1c00-6768-4000-8000-000000000001"), "en", "en", "English", "English", true, true, true, "/en", "/en", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) },
                    { new Guid("b42b1c00-6768-4000-8000-000000000002"), "ht", "ht", "Haitian Creole", "Kreyòl ayisyen", true, true, true, "/ht", "/ht", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) },
                    { new Guid("b42b1c00-6768-4000-8000-000000000003"), "es", "es", "Spanish", "Español", true, true, true, "/es", "/es", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) },
                    { new Guid("b42b1c00-6768-4000-8000-000000000004"), "fr", "fr", "French", "Français", true, true, true, "/fr", "/fr", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) },
                    { new Guid("b42b1c00-6768-4000-8000-000000000005"), "pt", "pt", "Portuguese", "Português", true, true, true, "/pt", "/pt", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) },
                    { new Guid("b42b1c00-6768-4000-8000-000000000006"), "de", "de", "German", "Deutsch", true, true, true, "/de", "/de", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) },
                    { new Guid("b42b1c00-6768-4000-8000-000000000007"), "ja", "ja", "Japanese", "日本語", true, true, true, "/ja", "/ja", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) },
                    { new Guid("b42b1c00-6768-4000-8000-000000000008"), "ko", "ko", "Korean", "한국어", true, true, true, "/ko", "/ko", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) },
                    { new Guid("b42b1c00-6768-4000-8000-000000000009"), "zh-Hans", "zh", "Chinese (Simplified)", "简体中文", true, true, true, "/zh-Hans", "/zh-Hans", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) },
                    { new Guid("b42b1c00-6768-4000-8000-000000000010"), "ar", "ar", "Arabic", "العربية", true, true, true, "/ar", "/ar", new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc), new DateTime(2026, 8, 11, 1, 31, 35, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageTextUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StoragePartition = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NormalizedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    GlobalConceptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsTrainingEligible = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageTextUnits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendTranslationAlignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: false),
                    SourceTextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetTextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProviderModel = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    QualityState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    HumanVerified = table.Column<bool>(type: "bit", nullable: false),
                    ObservationCount = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationAlignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendTranslationLearningEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: false),
                    SourceTextHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetTextHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceText = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: true),
                    TargetText = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EligibilityState = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProcessingState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationLearningEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendTranslationProviderCapacities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    BillingPeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    ConfiguredCapacityCharacters = table.Column<long>(type: "bigint", nullable: false),
                    ReservedLiveCharacters = table.Column<long>(type: "bigint", nullable: false),
                    LiveCharactersConsumed = table.Column<long>(type: "bigint", nullable: false),
                    BootstrapCharactersConsumed = table.Column<long>(type: "bigint", nullable: false),
                    TrainingCharactersConsumed = table.Column<long>(type: "bigint", nullable: false),
                    ReservedLiveCapacityCharacters = table.Column<long>(type: "bigint", nullable: false),
                    ProjectedLiveCharacters = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationProviderCapacities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCorpusCandidates_IdempotencyKey",
                table: "LegendCorpusCandidates",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendCorpusCandidates_IsApproved_ProcessingState_Priority_CreatedUtc",
                table: "LegendCorpusCandidates",
                columns: new[] { "IsApproved", "ProcessingState", "Priority", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCorpusCandidates_SourceLanguageCode_TargetLanguageCode",
                table: "LegendCorpusCandidates",
                columns: new[] { "SourceLanguageCode", "TargetLanguageCode" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendGlobalConcepts_ConceptKey",
                table: "LegendGlobalConcepts",
                column: "ConceptKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDefinitions_IsEnabled_IsTranslationEnabled",
                table: "LegendLanguageDefinitions",
                columns: new[] { "IsEnabled", "IsTranslationEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDefinitions_LanguageCode",
                table: "LegendLanguageDefinitions",
                column: "LanguageCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDefinitions_StoragePartition",
                table: "LegendLanguageDefinitions",
                column: "StoragePartition",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguagePairs_PairKey",
                table: "LegendLanguagePairs",
                column: "PairKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguagePairs_SourceLanguageCode_TargetLanguageCode",
                table: "LegendLanguagePairs",
                columns: new[] { "SourceLanguageCode", "TargetLanguageCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguagePairs_TranslationMemoryPartition",
                table: "LegendLanguagePairs",
                column: "TranslationMemoryPartition",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTextUnits_LanguageCode_NormalizedHash",
                table: "LegendLanguageTextUnits",
                columns: new[] { "LanguageCode", "NormalizedHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTextUnits_StoragePartition_CreatedUtc",
                table: "LegendLanguageTextUnits",
                columns: new[] { "StoragePartition", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationAlignments_PairKey_QualityState",
                table: "LegendTranslationAlignments",
                columns: new[] { "PairKey", "QualityState" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationAlignments_PairKey_SourceTextUnitId_TargetTextUnitId",
                table: "LegendTranslationAlignments",
                columns: new[] { "PairKey", "SourceTextUnitId", "TargetTextUnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationLearningEvents_IdempotencyKey",
                table: "LegendTranslationLearningEvents",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationLearningEvents_PairKey",
                table: "LegendTranslationLearningEvents",
                column: "PairKey");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationLearningEvents_ProcessingState_EligibilityState_CreatedUtc",
                table: "LegendTranslationLearningEvents",
                columns: new[] { "ProcessingState", "EligibilityState", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationProviderCapacities_Provider_BillingPeriodStart",
                table: "LegendTranslationProviderCapacities",
                columns: new[] { "Provider", "BillingPeriodStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendCorpusCandidates");

            migrationBuilder.DropTable(
                name: "LegendGlobalConcepts");

            migrationBuilder.DropTable(
                name: "LegendLanguageDefinitions");

            migrationBuilder.DropTable(
                name: "LegendLanguagePairs");

            migrationBuilder.DropTable(
                name: "LegendLanguageTextUnits");

            migrationBuilder.DropTable(
                name: "LegendTranslationAlignments");

            migrationBuilder.DropTable(
                name: "LegendTranslationLearningEvents");

            migrationBuilder.DropTable(
                name: "LegendTranslationProviderCapacities");
        }
    }
}
