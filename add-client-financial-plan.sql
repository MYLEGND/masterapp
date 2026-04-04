CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

BEGIN TRANSACTION;
CREATE TABLE "AgentClients" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_AgentClients" PRIMARY KEY,
    "AgentUserId" nvarchar(450) NOT NULL,
    "ClientUserId" nvarchar(450) NOT NULL,
    "AgentUpn" TEXT NOT NULL,
    "CreatedUtc" datetime2 NOT NULL
);

CREATE TABLE "ClientProfiles" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_ClientProfiles" PRIMARY KEY,
    "ClientUserId" nvarchar(450) NOT NULL,
    "FirstName" TEXT NOT NULL,
    "LastName" TEXT NOT NULL,
    "Email" TEXT NOT NULL,
    "Phone" TEXT NOT NULL,
    "DOB" datetime2 NULL,
    "MaritalStatus" TEXT NOT NULL,
    "SignificantOtherFirstName" TEXT NULL,
    "SignificantOtherLastName" TEXT NULL,
    "SignificantOtherDOB" datetime2 NULL,
    "SignificantOtherEmail" TEXT NULL,
    "SignificantOtherPhone" TEXT NULL,
    "AgentNotes" TEXT NOT NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

CREATE TABLE "HouseholdMembers" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_HouseholdMembers" PRIMARY KEY,
    "ClientUserId" nvarchar(450) NOT NULL,
    "RelationshipType" nvarchar(200) NOT NULL,
    "FirstName" TEXT NOT NULL,
    "LastName" TEXT NOT NULL,
    "DOB" datetime2 NULL,
    "Email" TEXT NOT NULL,
    "Phone" TEXT NOT NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

CREATE UNIQUE INDEX "IX_AgentClients_AgentUserId_ClientUserId" ON "AgentClients" ("AgentUserId", "ClientUserId");

CREATE UNIQUE INDEX "IX_ClientProfiles_ClientUserId" ON "ClientProfiles" ("ClientUserId");

CREATE INDEX "IX_HouseholdMembers_ClientUserId" ON "HouseholdMembers" ("ClientUserId");

CREATE UNIQUE INDEX "IX_HouseholdMembers_ClientUserId_RelationshipType" ON "HouseholdMembers" ("ClientUserId", "RelationshipType");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260209083538_20260209_InitialSqlServer', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "FinanceToolStates" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_FinanceToolStates" PRIMARY KEY,
    "ClientUserId" nvarchar(450) NOT NULL,
    "ToolKey" nvarchar(200) NOT NULL,
    "StateJson" TEXT NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

CREATE UNIQUE INDEX "IX_FinanceToolStates_ClientUserId_ToolKey" ON "FinanceToolStates" ("ClientUserId", "ToolKey");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260209085729_20260209_AddFinanceToolStates', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE UNIQUE INDEX "IX_ClientProfiles_Email" ON "ClientProfiles" ("Email");


DELETE FROM AgentClients
WHERE rowid IN (
    SELECT rowid
    FROM (
        SELECT rowid,
               ROW_NUMBER() OVER (
                   PARTITION BY ClientUserId
                   ORDER BY
                       CASE WHEN CreatedUtc IS NULL THEN 1 ELSE 0 END,
                       CreatedUtc ASC,
                       rowid ASC
               ) AS rn
        FROM AgentClients
    ) x
    WHERE x.rn > 1
);


CREATE UNIQUE INDEX "IX_AgentClients_ClientUserId" ON "AgentClients" ("ClientUserId");

CREATE TABLE "ef_temp_ClientProfiles" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_ClientProfiles" PRIMARY KEY,
    "AgentNotes" TEXT NOT NULL,
    "ClientUserId" nvarchar(450) NOT NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "DOB" datetime2 NULL,
    "Email" nvarchar(320) NOT NULL,
    "FirstName" TEXT NOT NULL,
    "LastName" TEXT NOT NULL,
    "MaritalStatus" TEXT NOT NULL,
    "Phone" TEXT NOT NULL,
    "SignificantOtherDOB" datetime2 NULL,
    "SignificantOtherEmail" TEXT NULL,
    "SignificantOtherFirstName" TEXT NULL,
    "SignificantOtherLastName" TEXT NULL,
    "SignificantOtherPhone" TEXT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

INSERT INTO "ef_temp_ClientProfiles" ("Id", "AgentNotes", "ClientUserId", "CreatedUtc", "DOB", "Email", "FirstName", "LastName", "MaritalStatus", "Phone", "SignificantOtherDOB", "SignificantOtherEmail", "SignificantOtherFirstName", "SignificantOtherLastName", "SignificantOtherPhone", "UpdatedUtc")
SELECT "Id", "AgentNotes", "ClientUserId", "CreatedUtc", "DOB", "Email", "FirstName", "LastName", "MaritalStatus", "Phone", "SignificantOtherDOB", "SignificantOtherEmail", "SignificantOtherFirstName", "SignificantOtherLastName", "SignificantOtherPhone", "UpdatedUtc"
FROM "ClientProfiles";

CREATE TABLE "ef_temp_AgentClients" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_AgentClients" PRIMARY KEY,
    "AgentUpn" nvarchar(320) NOT NULL,
    "AgentUserId" nvarchar(450) NOT NULL,
    "ClientUserId" nvarchar(450) NOT NULL,
    "CreatedUtc" datetime2 NOT NULL
);

INSERT INTO "ef_temp_AgentClients" ("Id", "AgentUpn", "AgentUserId", "ClientUserId", "CreatedUtc")
SELECT "Id", "AgentUpn", "AgentUserId", "ClientUserId", "CreatedUtc"
FROM "AgentClients";

COMMIT;

PRAGMA foreign_keys = 0;

BEGIN TRANSACTION;
DROP TABLE "ClientProfiles";

ALTER TABLE "ef_temp_ClientProfiles" RENAME TO "ClientProfiles";

DROP TABLE "AgentClients";

ALTER TABLE "ef_temp_AgentClients" RENAME TO "AgentClients";

COMMIT;

PRAGMA foreign_keys = 1;

BEGIN TRANSACTION;
CREATE UNIQUE INDEX "IX_ClientProfiles_ClientUserId" ON "ClientProfiles" ("ClientUserId");

CREATE UNIQUE INDEX "IX_ClientProfiles_Email" ON "ClientProfiles" ("Email");

CREATE UNIQUE INDEX "IX_AgentClients_AgentUserId_ClientUserId" ON "AgentClients" ("AgentUserId", "ClientUserId");

CREATE UNIQUE INDEX "IX_AgentClients_ClientUserId" ON "AgentClients" ("ClientUserId");

COMMIT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260210135445_ProfileAndHouseholdHardening', '10.0.2');

BEGIN TRANSACTION;
CREATE TABLE "RecurringExpenses" (
    "Id" int NOT NULL CONSTRAINT "PK_RecurringExpenses" PRIMARY KEY,
    "AgentUserId" nvarchar(450) NOT NULL,
    "Name" nvarchar(120) NOT NULL,
    "Amount" decimal(18,2) NOT NULL,
    "Frequency" int NOT NULL,
    "Category" int NOT NULL,
    "StartDate" datetime2 NOT NULL,
    "NextDueDate" datetime2 NULL,
    "IsActive" bit NOT NULL,
    "Notes" nvarchar(240) NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

CREATE TABLE "BookkeepingEntries" (
    "Id" int NOT NULL CONSTRAINT "PK_BookkeepingEntries" PRIMARY KEY,
    "AgentUserId" nvarchar(450) NOT NULL,
    "Type" int NOT NULL,
    "EntryDate" datetime2 NOT NULL,
    "Amount" decimal(18,2) NOT NULL,
    "Category" int NOT NULL,
    "Notes" nvarchar(240) NULL,
    "RecurringExpenseId" int NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL,
    CONSTRAINT "FK_BookkeepingEntries_RecurringExpenses_RecurringExpenseId" FOREIGN KEY ("RecurringExpenseId") REFERENCES "RecurringExpenses" ("Id") ON DELETE SET NULL
);

CREATE INDEX "IX_BookkeepingEntries_AgentUserId_EntryDate" ON "BookkeepingEntries" ("AgentUserId", "EntryDate");

CREATE INDEX "IX_BookkeepingEntries_RecurringExpenseId" ON "BookkeepingEntries" ("RecurringExpenseId");

CREATE INDEX "IX_RecurringExpenses_AgentUserId_IsActive" ON "RecurringExpenses" ("AgentUserId", "IsActive");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260210213538_20260210_AddBookkeeping', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
DROP INDEX "IX_RecurringExpenses_AgentUserId_IsActive";

DROP INDEX "IX_BookkeepingEntries_AgentUserId_EntryDate";

ALTER TABLE "RecurringExpenses" ADD "OwnerUserId" TEXT NOT NULL DEFAULT '';

ALTER TABLE "BookkeepingEntries" ADD "OwnerUserId" TEXT NOT NULL DEFAULT '';

CREATE INDEX "IX_RecurringExpenses_OwnerUserId_IsActive" ON "RecurringExpenses" ("OwnerUserId", "IsActive");

CREATE INDEX "IX_BookkeepingEntries_OwnerUserId_EntryDate" ON "BookkeepingEntries" ("OwnerUserId", "EntryDate");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260212200153_AddOwnerUserIdToBookkeeping', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
DROP INDEX "IX_RecurringExpenses_OwnerUserId_IsActive";

DROP INDEX "IX_BookkeepingEntries_OwnerUserId_EntryDate";

ALTER TABLE "RecurringExpenses" ADD "Scope" int NOT NULL DEFAULT 0;

ALTER TABLE "BookkeepingEntries" ADD "Scope" int NOT NULL DEFAULT 0;

CREATE INDEX "IX_RecurringExpenses_OwnerUserId_Scope_IsActive" ON "RecurringExpenses" ("OwnerUserId", "Scope", "IsActive");

CREATE INDEX "IX_BookkeepingEntries_OwnerUserId_Scope_EntryDate" ON "BookkeepingEntries" ("OwnerUserId", "Scope", "EntryDate");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260212221532_ScopeAlignment', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260213015112_InitialBaseline', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "RecurringExpenses" ADD "Type" int NOT NULL DEFAULT 0;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260217173126_20260217_ModelSync', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260217223951_20260217_AddRecurringType', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260217224629_20260217_ManualAddRecurringType', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "ClientProfiles" ADD "CrmLastTouch" datetime2 NULL;

ALTER TABLE "ClientProfiles" ADD "CrmNextDate" datetime2 NULL;

ALTER TABLE "ClientProfiles" ADD "CrmNextText" TEXT NULL;

ALTER TABLE "ClientProfiles" ADD "CrmNotes" TEXT NULL;

ALTER TABLE "ClientProfiles" ADD "CrmPriority" TEXT NULL;

ALTER TABLE "ClientProfiles" ADD "CrmStatus" TEXT NULL;

ALTER TABLE "ClientProfiles" ADD "CrmTags" TEXT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260305042555_AddClientCrmFields', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
DROP TABLE IF EXISTS "FinanceToolStates";

CREATE TABLE "FinanceToolStates" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_FinanceToolStates" PRIMARY KEY,
    "ClientProfileId" uniqueidentifier NOT NULL,
    "ToolId" nvarchar(100) NOT NULL,
    "JsonState" TEXT NOT NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

CREATE UNIQUE INDEX "IX_FinanceToolStates_ClientProfileId_ToolId" ON "FinanceToolStates" ("ClientProfileId", "ToolId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260306091641_AddFinanceToolStatesClean', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260306092018_AddFinanceToolStatesFresh', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "ScriptLeadProfiles" (
    "LeadId" nvarchar(64) NOT NULL CONSTRAINT "PK_ScriptLeadProfiles" PRIMARY KEY,
    "Bucket" nvarchar(80) NOT NULL,
    "FirstName" nvarchar(120) NOT NULL,
    "LastName" nvarchar(120) NOT NULL,
    "Email" nvarchar(320) NOT NULL,
    "Phone" nvarchar(60) NOT NULL,
    "Phone2" nvarchar(60) NULL,
    "AddressLine" nvarchar(240) NULL,
    "City" nvarchar(160) NULL,
    "State" nvarchar(40) NULL,
    "County" nvarchar(120) NULL,
    "ZipCode" nvarchar(24) NULL,
    "DOB" datetime2 NULL,
    "Gender" nvarchar(20) NULL,
    "MortgageLender" nvarchar(160) NULL,
    "LoanAmount" nvarchar(80) NULL,
    "CrmStatus" nvarchar(60) NOT NULL,
    "CrmStage" nvarchar(80) NOT NULL,
    "CrmOrder" bigint NOT NULL,
    "CrmNotes" TEXT NULL,
    "CallCount" int NOT NULL DEFAULT 0,
    "CreatedUtc" datetime2 NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

CREATE INDEX "IX_ScriptLeadProfiles_Bucket" ON "ScriptLeadProfiles" ("Bucket");

CREATE INDEX "IX_ScriptLeadProfiles_Email" ON "ScriptLeadProfiles" ("Email");

CREATE INDEX "IX_ScriptLeadProfiles_Phone" ON "ScriptLeadProfiles" ("Phone");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260310190000_CreateScriptLeadProfiles', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "ScriptLeadProfiles" ADD "OpportunityPlanningJson" TEXT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260311113000_AddLeadOpportunityPlanningJson', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "ScriptLeadProfiles" ADD "AgentUserId" nvarchar(450) NOT NULL DEFAULT '';

CREATE INDEX "IX_ScriptLeadProfiles_AgentUserId" ON "ScriptLeadProfiles" ("AgentUserId");

CREATE INDEX "IX_ScriptLeadProfiles_AgentUserId_Phone" ON "ScriptLeadProfiles" ("AgentUserId", "Phone");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260311123000_AddAgentUserIdToScriptLeadProfiles', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "ScriptLeadProfiles" ADD "Age" nvarchar(12) NULL;

ALTER TABLE "ScriptLeadProfiles" ADD "Btc" nvarchar(40) NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260311140000_AddAgeAndBtcToScriptLeadProfiles', '10.0.2');

COMMIT;

BEGIN TRANSACTION;

UPDATE ScriptLeadProfiles
SET AgentUserId = '9a578496-dafd-4c10-aba5-a8386da25e53'
WHERE AgentUserId IS NULL
   OR LTRIM(RTRIM(AgentUserId)) = '';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260312090000_BackfillAgentUserIdForZac', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
DROP INDEX IF EXISTS "IX_ClientProfiles_Email";

CREATE UNIQUE INDEX "IX_ClientProfiles_Email" ON "ClientProfiles" ("Email");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260315000000_AddUniqueIndex_ClientProfile_Email', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "ClientProfiles" ADD "NormalizedEmail" nvarchar(320) NULL;


UPDATE ClientProfiles
SET NormalizedEmail = LOWER(LTRIM(RTRIM(Email)))
WHERE Email IS NOT NULL;


DROP INDEX IF EXISTS "IX_ClientProfiles_NormalizedEmail";

CREATE UNIQUE INDEX "IX_ClientProfiles_NormalizedEmail" ON "ClientProfiles" ("NormalizedEmail") WHERE [NormalizedEmail] IS NOT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260315010000_AddNormalizedEmailUnique', '10.0.2');

COMMIT;

BEGIN TRANSACTION;

ALTER TABLE "ScriptLeadProfiles" RENAME TO "WorkstationLeadProfiles";


INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260315090000_RenameScriptLeadProfilesToWorkstation', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260315153000_CleanupLegacyLeadPlaceholderEmails', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "WorkstationLeadProfiles" ADD "CallsMonth" int NOT NULL DEFAULT 0;

ALTER TABLE "WorkstationLeadProfiles" ADD "CallsMonthStartUtc" datetime2 NULL;

ALTER TABLE "WorkstationLeadProfiles" ADD "CallsToday" int NOT NULL DEFAULT 0;

ALTER TABLE "WorkstationLeadProfiles" ADD "CallsTodayDateUtc" datetime2 NULL;

ALTER TABLE "WorkstationLeadProfiles" ADD "CallsWeek" int NOT NULL DEFAULT 0;

ALTER TABLE "WorkstationLeadProfiles" ADD "CallsWeekStartUtc" datetime2 NULL;

ALTER TABLE "WorkstationLeadProfiles" ADD "CallsYear" int NOT NULL DEFAULT 0;

ALTER TABLE "WorkstationLeadProfiles" ADD "CallsYearStartUtc" datetime2 NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260315180000_AddWorkstationLeadAttemptWindows', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260315190000_BackfillLegacyWorkstationLeadOriginalType', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "WorkstationLeadProfiles" ADD "OriginalLeadType" nvarchar(80) NULL;

UPDATE "WorkstationLeadProfiles" SET "OriginalLeadType" = "Bucket" WHERE "OriginalLeadType" IS NULL OR TRIM("OriginalLeadType") = '';

CREATE INDEX IF NOT EXISTS "IX_WorkstationLeadProfiles_OriginalLeadType" ON "WorkstationLeadProfiles" ("OriginalLeadType");

CREATE INDEX IF NOT EXISTS "IX_WorkstationLeadProfiles_AgentUserId_OriginalLeadType" ON "WorkstationLeadProfiles" ("AgentUserId", "OriginalLeadType");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260318012000_AddMissingOriginalLeadTypeToWorkstation', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "Proposals" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_Proposals" PRIMARY KEY,
    "LeadId" nvarchar(128) NOT NULL,
    "AgentUserId" nvarchar(450) NOT NULL,
    "LeadName" nvarchar(240) NULL,
    "Name" nvarchar(200) NOT NULL,
    "BucketsJson" TEXT NOT NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

CREATE INDEX "IX_Proposals_AgentUserId" ON "Proposals" ("AgentUserId");

CREATE INDEX "IX_Proposals_AgentUserId_LeadId" ON "Proposals" ("AgentUserId", "LeadId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260318143000_AddProposalsServerBacking', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "Proposals" ADD "IsDraft" bit NOT NULL DEFAULT 0;

ALTER TABLE "Proposals" ADD "LeadKey" TEXT NULL;

ALTER TABLE "Proposals" ADD "PageTitle" TEXT NULL;

ALTER TABLE "Proposals" ADD "QueueKey" TEXT NULL;

ALTER TABLE "Proposals" ADD "ScopeKey" TEXT NULL;

CREATE TABLE "UnderwritingRecords" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_UnderwritingRecords" PRIMARY KEY,
    "LeadId" nvarchar(128) NULL,
    "LeadName" nvarchar(240) NULL,
    "AgentUserId" nvarchar(450) NOT NULL,
    "Name" nvarchar(200) NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "ProductCode" nvarchar(32) NULL,
    "QueueKey" nvarchar(80) NULL,
    "ScopeKey" nvarchar(200) NULL,
    "PageTitle" nvarchar(240) NULL,
    "IsDraft" bit NOT NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

CREATE INDEX "IX_UnderwritingRecords_AgentUserId" ON "UnderwritingRecords" ("AgentUserId");

CREATE INDEX "IX_UnderwritingRecords_AgentUserId_LeadId" ON "UnderwritingRecords" ("AgentUserId", "LeadId");

CREATE INDEX "IX_UnderwritingRecords_ProductCode" ON "UnderwritingRecords" ("ProductCode");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260319033500_AddUnderwritingServerBacking', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260319141942_20260319_SnapshotSync', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "OnboardingInvites" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_OnboardingInvites" PRIMARY KEY,
    "TokenHash" TEXT NOT NULL,
    "FirstName" TEXT NOT NULL,
    "LastName" TEXT NOT NULL,
    "Email" TEXT NOT NULL,
    "RoleType" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "ExpiresUtc" TEXT NULL,
    "SubmittedUtc" TEXT NULL,
    "RevokedUtc" TEXT NULL,
    "CreatedBy" TEXT NULL
);

CREATE TABLE "OnboardingSubmissions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_OnboardingSubmissions" PRIMARY KEY,
    "InviteId" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "SubmittedUtc" TEXT NULL,
    "FirstName" TEXT NOT NULL,
    "MiddleName" TEXT NULL,
    "LastName" TEXT NOT NULL,
    "PreferredName" TEXT NULL,
    "DateOfBirth" TEXT NULL,
    "Phone" TEXT NOT NULL,
    "Email" TEXT NOT NULL,
    "CurrentAddress" TEXT NOT NULL,
    "City" TEXT NOT NULL,
    "State" TEXT NOT NULL,
    "Zip" TEXT NULL,
    "MailingAddress" TEXT NULL,
    "EmergencyContactName" TEXT NOT NULL,
    "EmergencyContactPhone" TEXT NOT NULL,
    "EmergencyContactRelationship" TEXT NOT NULL,
    "RoleType" TEXT NOT NULL,
    "JobTitle" TEXT NOT NULL,
    "Department" TEXT NULL,
    "Manager" TEXT NULL,
    "StartDate" TEXT NULL,
    "WorkState" TEXT NULL,
    "WorkLocation" TEXT NULL,
    "EmploymentType" TEXT NOT NULL,
    "PayType" TEXT NOT NULL,
    "WorkNotes" text NULL,
    "LegalNameConfirmed" INTEGER NOT NULL,
    "SsnLast4" TEXT NULL,
    "SsnNote" TEXT NULL,
    "DriverLicenseNumber" TEXT NULL,
    "DriverLicenseState" TEXT NULL,
    "WorkAuthorizationStatus" TEXT NOT NULL,
    "CitizenshipStatus" TEXT NULL,
    "EligibilityDocumentsAck" INTEGER NOT NULL,
    "TaxFilingStatus" TEXT NOT NULL,
    "FederalWithholding" TEXT NULL,
    "StateWithholding" TEXT NULL,
    "BankName" TEXT NOT NULL,
    "BankAccountType" TEXT NOT NULL,
    "BankRoutingNumber" TEXT NOT NULL,
    "BankAccountNumber" TEXT NOT NULL,
    "PayrollAcknowledgement" INTEGER NOT NULL,
    "ConfidentialityAck" INTEGER NOT NULL,
    "HandbookAck" INTEGER NOT NULL,
    "TechnologyAck" INTEGER NOT NULL,
    "ComplianceAck" INTEGER NOT NULL,
    "CompensationAck" INTEGER NOT NULL,
    "NonSolicitAck" INTEGER NOT NULL,
    "ElectronicSignatureAck" INTEGER NOT NULL,
    "ElectronicSignatureName" TEXT NOT NULL,
    "ElectronicSignatureDate" TEXT NULL,
    "ResidentStateLicense" TEXT NULL,
    "NonResidentStates" TEXT NULL,
    "LicensesHeld" TEXT NULL,
    "LicenseNumbers" TEXT NULL,
    "CarrierAppointments" TEXT NULL,
    "EOCoverage" TEXT NULL,
    "SupervisionNotes" text NULL,
    "HasRegulatoryIssues" INTEGER NULL,
    "RegulatoryExplanation" text NULL,
    "HasCriminalHistory" INTEGER NULL,
    "CriminalExplanation" text NULL,
    "HasAdministrativeActions" INTEGER NULL,
    "AdministrativeExplanation" text NULL,
    "HasPriorTermination" INTEGER NULL,
    "TerminationExplanation" text NULL,
    "HasOtherDisclosures" INTEGER NULL,
    "OtherDisclosuresExplanation" text NULL,
    "HasIdDocument" INTEGER NULL,
    "HasSsnDocument" INTEGER NULL,
    "HasVoidedCheck" INTEGER NULL,
    "HasLicenseCopy" INTEGER NULL,
    "HasCertifications" INTEGER NULL,
    "HasResume" INTEGER NULL,
    "HasSignedAgreements" INTEGER NULL,
    "DocumentNotes" text NULL,
    "CertificationTruthful" INTEGER NOT NULL,
    CONSTRAINT "FK_OnboardingSubmissions_OnboardingInvites_InviteId" FOREIGN KEY ("InviteId") REFERENCES "OnboardingInvites" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_OnboardingInvites_TokenHash" ON "OnboardingInvites" ("TokenHash");

CREATE UNIQUE INDEX "IX_OnboardingSubmissions_InviteId" ON "OnboardingSubmissions" ("InviteId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260321_AddOnboarding', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "AgentAssistants" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_AgentAssistants" PRIMARY KEY,
    "ParentAgentUserId" TEXT NOT NULL,
    "AssistantUserId" TEXT NULL,
    "FirstName" TEXT NOT NULL,
    "LastName" TEXT NOT NULL,
    "Email" TEXT NOT NULL,
    "IsActive" INTEGER NOT NULL,
    "InvitedAt" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL
);

CREATE UNIQUE INDEX "IX_AgentAssistants_AssistantUserId" ON "AgentAssistants" ("AssistantUserId");

CREATE INDEX "IX_AgentAssistants_ParentAgentUserId" ON "AgentAssistants" ("ParentAgentUserId");

CREATE UNIQUE INDEX "IX_AgentAssistants_ParentAgentUserId_Email" ON "AgentAssistants" ("ParentAgentUserId", "Email");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260321130615_AddAgentAssistantsRuntimeFix', '10.0.2');

COMMIT;

BEGIN TRANSACTION;

CREATE TABLE IF NOT EXISTS "AgentProfiles" (
    "Id" TEXT NOT NULL CONSTRAINT PK_AgentProfiles PRIMARY KEY,
    "AgentUserId" TEXT NOT NULL,
    "AgentUpn" TEXT,
    "FullName" TEXT,
    "Title" TEXT,
    "Npn" TEXT,
    "Phone" TEXT,
    "DisplayOrder" INTEGER,
    "CreatedUtc" TEXT NOT NULL DEFAULT (datetime('now')),
    "UpdatedUtc" TEXT NOT NULL DEFAULT (datetime('now'))
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260322051537_AddAgentPhoneToProfile', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260322113000_AddAgentTitleToProfile', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "ProductionRecords" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ProductionRecords" PRIMARY KEY,
    "AgentUserId" TEXT NOT NULL,
    "Side" INTEGER NOT NULL,
    "Status" INTEGER NOT NULL,
    "Amount" decimal(18,2) NOT NULL,
    "LeadId" TEXT NULL,
    "ClientUserId" TEXT NULL,
    "Notes" TEXT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "UpdatedUtc" TEXT NOT NULL
);

CREATE INDEX "IX_ProductionRecords_AgentUserId" ON "ProductionRecords" ("AgentUserId");

CREATE INDEX "IX_ProductionRecords_AgentUserId_Side" ON "ProductionRecords" ("AgentUserId", "Side");

CREATE INDEX "IX_ProductionRecords_ClientUserId" ON "ProductionRecords" ("ClientUserId");

CREATE INDEX "IX_ProductionRecords_LeadId" ON "ProductionRecords" ("LeadId");

CREATE INDEX "IX_ProductionRecords_Status" ON "ProductionRecords" ("Status");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260323090000_AddProductionRecords', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260324083000_AddAgentProfileDisplayOrder', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "WebsiteLeads" (
    "Id" bigint NOT NULL CONSTRAINT "PK_WebsiteLeads" PRIMARY KEY,
    "LeadId" uniqueidentifier NOT NULL,
    "FirstName" TEXT NOT NULL,
    "LastName" TEXT NULL,
    "Email" TEXT NOT NULL,
    "Phone" TEXT NULL,
    "PreferredContactMethod" TEXT NULL,
    "InterestType" TEXT NULL,
    "Notes" TEXT NULL,
    "SourcePageKey" TEXT NULL,
    "SourceCtaKey" TEXT NULL,
    "UtmSource" TEXT NULL,
    "UtmMedium" TEXT NULL,
    "UtmCampaign" TEXT NULL,
    "SessionId" TEXT NULL,
    "VisitorId" TEXT NULL,
    "MarketingEmailConsent" bit NOT NULL,
    "CallTextConsent" bit NOT NULL,
    "TermsAccepted" bit NOT NULL,
    "IsInternal" bit NOT NULL,
    "Environment" TEXT NULL,
    "Host" TEXT NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "Status" TEXT NOT NULL,
    "MetadataJson" TEXT NULL
);

CREATE INDEX "IX_WebsiteLeads_CreatedUtc" ON "WebsiteLeads" ("CreatedUtc");

CREATE INDEX "IX_WebsiteLeads_Email" ON "WebsiteLeads" ("Email");

CREATE INDEX "IX_WebsiteLeads_InterestType" ON "WebsiteLeads" ("InterestType");

CREATE INDEX "IX_WebsiteLeads_SourceCtaKey" ON "WebsiteLeads" ("SourceCtaKey");

CREATE INDEX "IX_WebsiteLeads_SourcePageKey" ON "WebsiteLeads" ("SourcePageKey");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260327121000_CreateWebsiteLeadManual', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "AnalyticsEvents" (
    "Id" bigint NOT NULL CONSTRAINT "PK_AnalyticsEvents" PRIMARY KEY,
    "EventId" uniqueidentifier NOT NULL,
    "ClientEventId" uniqueidentifier NULL,
    "EventType" nvarchar(120) NOT NULL,
    "PageKey" nvarchar(120) NULL,
    "SectionKey" nvarchar(120) NULL,
    "ElementKey" nvarchar(160) NULL,
    "ButtonLabel" nvarchar(200) NULL,
    "FormKey" nvarchar(120) NULL,
    "QuoteType" nvarchar(120) NULL,
    "Url" nvarchar(1024) NULL,
    "Path" nvarchar(512) NULL,
    "Referrer" nvarchar(1024) NULL,
    "SessionId" nvarchar(120) NULL,
    "VisitorId" nvarchar(120) NULL,
    "UtmSource" nvarchar(160) NULL,
    "UtmMedium" nvarchar(160) NULL,
    "UtmCampaign" nvarchar(160) NULL,
    "IsInternal" bit NOT NULL,
    "Environment" nvarchar(40) NULL,
    "Host" nvarchar(160) NULL,
    "EventUtc" datetime2 NOT NULL,
    "ReceivedUtc" datetime2 NOT NULL,
    "SubmitOutcome" nvarchar(80) NULL,
    "MetadataJson" TEXT NULL
);

CREATE INDEX "IX_AnalyticsEvents_ElementKey" ON "AnalyticsEvents" ("ElementKey");

CREATE INDEX "IX_AnalyticsEvents_EventType" ON "AnalyticsEvents" ("EventType");

CREATE INDEX "IX_AnalyticsEvents_FormKey" ON "AnalyticsEvents" ("FormKey");

CREATE INDEX "IX_AnalyticsEvents_PageKey" ON "AnalyticsEvents" ("PageKey");

CREATE INDEX "IX_AnalyticsEvents_ReceivedUtc" ON "AnalyticsEvents" ("ReceivedUtc");

CREATE INDEX "IX_AnalyticsEvents_SessionId" ON "AnalyticsEvents" ("SessionId");

CREATE INDEX "IX_AnalyticsEvents_VisitorId" ON "AnalyticsEvents" ("VisitorId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260327121010_CreateAnalyticsEventsManual', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "AgentTrackingProfiles" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_AgentTrackingProfiles" PRIMARY KEY,
    "AgentUserId" nvarchar(450) NOT NULL,
    "AgentUpn" nvarchar(450) NOT NULL,
    "Slug" nvarchar(200) NOT NULL,
    "DisplayName" nvarchar(200) NULL,
    "Status" nvarchar(40) NOT NULL,
    "PreferredEnvironment" nvarchar(40) NULL,
    "CreatedUtc" datetime2 NOT NULL,
    "UpdatedUtc" datetime2 NOT NULL
);

CREATE TABLE "AgentTrackingAliases" (
    "Id" uniqueidentifier NOT NULL CONSTRAINT "PK_AgentTrackingAliases" PRIMARY KEY,
    "AgentTrackingProfileId" uniqueidentifier NOT NULL,
    "Slug" nvarchar(200) NOT NULL,
    "IsCanonical" bit NOT NULL,
    "CreatedUtc" datetime2 NOT NULL,
    CONSTRAINT "FK_AgentTrackingAliases_AgentTrackingProfiles_AgentTrackingProfileId" FOREIGN KEY ("AgentTrackingProfileId") REFERENCES "AgentTrackingProfiles" ("Id") ON DELETE CASCADE
);

ALTER TABLE "WebsiteLeads" ADD "AgentTrackingProfileId" uniqueidentifier NULL;

ALTER TABLE "WebsiteLeads" ADD "AgentSlug" nvarchar(200) NULL;

ALTER TABLE "AnalyticsEvents" ADD "AgentTrackingProfileId" uniqueidentifier NULL;

ALTER TABLE "AnalyticsEvents" ADD "AgentSlug" nvarchar(200) NULL;

CREATE INDEX "IX_AnalyticsEvents_AgentTrackingProfileId" ON "AnalyticsEvents" ("AgentTrackingProfileId");

CREATE INDEX "IX_AnalyticsEvents_AgentSlug" ON "AnalyticsEvents" ("AgentSlug");

CREATE INDEX "IX_WebsiteLeads_AgentTrackingProfileId" ON "WebsiteLeads" ("AgentTrackingProfileId");

CREATE INDEX "IX_WebsiteLeads_AgentSlug" ON "WebsiteLeads" ("AgentSlug");

CREATE INDEX "IX_AgentTrackingAliases_AgentTrackingProfileId_IsCanonical" ON "AgentTrackingAliases" ("AgentTrackingProfileId", "IsCanonical");

CREATE UNIQUE INDEX "IX_AgentTrackingAliases_Slug" ON "AgentTrackingAliases" ("Slug");

CREATE UNIQUE INDEX "IX_AgentTrackingProfiles_AgentUserId" ON "AgentTrackingProfiles" ("AgentUserId");

CREATE INDEX "IX_AgentTrackingProfiles_AgentUpn" ON "AgentTrackingProfiles" ("AgentUpn");

CREATE UNIQUE INDEX "IX_AgentTrackingProfiles_Slug" ON "AgentTrackingProfiles" ("Slug");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260327225000_AddAgentTrackingProfiles', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260328071506_AddAnalyticsScaleIndexes', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260330000618_ExecutionMvp_Regen', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260330011403_ActionSurfaceSeparation', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
CREATE TABLE "Commitments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Commitments" PRIMARY KEY,
    "RelatedEntityType" TEXT NOT NULL,
    "RelatedEntityId" TEXT NOT NULL,
    "PromisedByType" TEXT NOT NULL,
    "PromisedById" TEXT NOT NULL,
    "PromisedToType" TEXT NOT NULL,
    "PromisedToId" TEXT NOT NULL,
    "PromiseText" TEXT NOT NULL,
    "DueDateUtc" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "LinkedActionId" TEXT NULL,
    "CreatedBy" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "FulfilledAtUtc" TEXT NULL
);

CREATE INDEX "IX_Commitments_DueDateUtc_Status" ON "Commitments" ("DueDateUtc", "Status");

CREATE INDEX "IX_Commitments_PromisedById_Status" ON "Commitments" ("PromisedById", "Status");

CREATE INDEX "IX_Commitments_RelatedEntityType_RelatedEntityId" ON "Commitments" ("RelatedEntityType", "RelatedEntityId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260331000000_CommitmentsMvp', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "OnboardingInvites" ADD "NormalizedEmail" nvarchar(320) NULL;

ALTER TABLE "AgentProfiles" ADD "NormalizedEmail" nvarchar(320) NULL;

ALTER TABLE "AgentAssistants" ADD "NormalizedEmail" nvarchar(320) NULL;

UPDATE AgentProfiles SET NormalizedEmail = LOWER(LTRIM(RTRIM(AgentUpn))) WHERE NormalizedEmail IS NULL AND AgentUpn IS NOT NULL AND LTRIM(RTRIM(AgentUpn)) <> '';

UPDATE ClientProfiles SET NormalizedEmail = LOWER(LTRIM(RTRIM(Email))) WHERE NormalizedEmail IS NULL AND Email IS NOT NULL AND LTRIM(RTRIM(Email)) <> '';

UPDATE AgentAssistants SET NormalizedEmail = LOWER(LTRIM(RTRIM(Email))) WHERE NormalizedEmail IS NULL AND Email IS NOT NULL AND LTRIM(RTRIM(Email)) <> '';

UPDATE OnboardingInvites SET NormalizedEmail = LOWER(LTRIM(RTRIM(Email))) WHERE NormalizedEmail IS NULL AND Email IS NOT NULL AND LTRIM(RTRIM(Email)) <> '';

CREATE UNIQUE INDEX "IX_OnboardingInvites_NormalizedEmail" ON "OnboardingInvites" ("NormalizedEmail") WHERE [NormalizedEmail] IS NOT NULL;

CREATE UNIQUE INDEX "IX_AgentProfiles_NormalizedEmail" ON "AgentProfiles" ("NormalizedEmail") WHERE [NormalizedEmail] IS NOT NULL;

CREATE UNIQUE INDEX "IX_AgentAssistants_NormalizedEmail" ON "AgentAssistants" ("NormalizedEmail") WHERE [NormalizedEmail] IS NOT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260331130000_IdentityEmailNormalization', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
DROP INDEX "IX_ClientProfiles_Email";

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260401000000_Stage1IdentityHardening', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260401012634_PiiFieldEncryption', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260401023834_AddProductionRecords_PersonalAmount', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "WorkstationLeadProfiles" ADD "RowVersion" BLOB NOT NULL DEFAULT X'';

ALTER TABLE "ProductionRecords" ADD "RowVersion" BLOB NOT NULL DEFAULT X'';

ALTER TABLE "ClientProfiles" ADD "RowVersion" BLOB NOT NULL DEFAULT X'';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260401061601_AddRowVersionConcurrency', '10.0.2');

COMMIT;

BEGIN TRANSACTION;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260401070000_AddRowVersionConcurrency_SqlServer', '10.0.2');

COMMIT;

