using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContextAttribute(typeof(MasterAppDbContext))]
    [Migration("20260321_AddOnboarding")]
    public partial class AddOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnboardingInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    RoleType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubmittedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingInvites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InviteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    MiddleName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PreferredName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    DateOfBirth = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CurrentAddress = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    City = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Zip = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    MailingAddress = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    EmergencyContactName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    EmergencyContactPhone = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    EmergencyContactRelationship = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    RoleType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    JobTitle = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Department = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Manager = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WorkState = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    WorkLocation = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EmploymentType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PayType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    WorkNotes = table.Column<string>(type: "text", nullable: true),
                    LegalNameConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SsnLast4 = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    SsnNote = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    DriverLicenseNumber = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    DriverLicenseState = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    WorkAuthorizationStatus = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CitizenshipStatus = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    EligibilityDocumentsAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    TaxFilingStatus = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    FederalWithholding = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    StateWithholding = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    BankName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    BankAccountType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    BankRoutingNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BankAccountNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PayrollAcknowledgement = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfidentialityAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    HandbookAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    TechnologyAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    ComplianceAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompensationAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    NonSolicitAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    ElectronicSignatureAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    ElectronicSignatureName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ElectronicSignatureDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResidentStateLicense = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    NonResidentStates = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    LicensesHeld = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    LicenseNumbers = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CarrierAppointments = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    EOCoverage = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    SupervisionNotes = table.Column<string>(type: "text", nullable: true),
                    HasRegulatoryIssues = table.Column<bool>(type: "INTEGER", nullable: true),
                    RegulatoryExplanation = table.Column<string>(type: "text", nullable: true),
                    HasCriminalHistory = table.Column<bool>(type: "INTEGER", nullable: true),
                    CriminalExplanation = table.Column<string>(type: "text", nullable: true),
                    HasAdministrativeActions = table.Column<bool>(type: "INTEGER", nullable: true),
                    AdministrativeExplanation = table.Column<string>(type: "text", nullable: true),
                    HasPriorTermination = table.Column<bool>(type: "INTEGER", nullable: true),
                    TerminationExplanation = table.Column<string>(type: "text", nullable: true),
                    HasOtherDisclosures = table.Column<bool>(type: "INTEGER", nullable: true),
                    OtherDisclosuresExplanation = table.Column<string>(type: "text", nullable: true),
                    HasIdDocument = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasSsnDocument = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasVoidedCheck = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasLicenseCopy = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasCertifications = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasResume = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasSignedAgreements = table.Column<bool>(type: "INTEGER", nullable: true),
                    DocumentNotes = table.Column<string>(type: "text", nullable: true),
                    CertificationTruthful = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnboardingSubmissions_OnboardingInvites_InviteId",
                        column: x => x.InviteId,
                        principalTable: "OnboardingInvites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingInvites_TokenHash",
                table: "OnboardingInvites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingSubmissions_InviteId",
                table: "OnboardingSubmissions",
                column: "InviteId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnboardingSubmissions");

            migrationBuilder.DropTable(
                name: "OnboardingInvites");
        }
    }
}
