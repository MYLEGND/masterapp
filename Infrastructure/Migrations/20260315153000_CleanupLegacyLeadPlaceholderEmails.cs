using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260315153000_CleanupLegacyLeadPlaceholderEmails")]
    public partial class CleanupLegacyLeadPlaceholderEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
IF OBJECT_ID(N'dbo.WorkstationLeadProfiles', N'U') IS NOT NULL
BEGIN
    DECLARE @FoundCount INT = (
        SELECT COUNT(*)
        FROM dbo.WorkstationLeadProfiles
        WHERE LOWER(LTRIM(RTRIM(ISNULL(Email, '')))) LIKE 'lead-%@scripts.local'
    );

    DECLARE @SkippedClientProfiles INT = CASE
        WHEN OBJECT_ID(N'dbo.ClientProfiles', N'U') IS NULL THEN 0
        ELSE (
            SELECT COUNT(*)
            FROM dbo.ClientProfiles
            WHERE LOWER(LTRIM(RTRIM(ISNULL(Email, '')))) LIKE 'lead-%@scripts.local'
        )
    END;

    PRINT CONCAT('Legacy CRM placeholder emails found in WorkstationLeadProfiles: ', @FoundCount);
    PRINT CONCAT('Legacy placeholder emails skipped in ClientProfiles (safety scope): ', @SkippedClientProfiles);

    IF @FoundCount > 0
    BEGIN
        DECLARE
            @LeadId NVARCHAR(64),
            @LastName NVARCHAR(120),
            @Slug NVARCHAR(120),
            @Source NVARCHAR(120),
            @Candidate NVARCHAR(320),
            @Suffix INT,
            @Index INT,
            @Len INT,
            @Ch NCHAR(1),
            @UpdatedCount INT = 0;

        DECLARE lead_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT LeadId, LastName
            FROM dbo.WorkstationLeadProfiles
            WHERE LOWER(LTRIM(RTRIM(ISNULL(Email, '')))) LIKE 'lead-%@scripts.local'
            ORDER BY ISNULL(LastName, ''), ISNULL(FirstName, ''), LeadId;

        OPEN lead_cursor;
        FETCH NEXT FROM lead_cursor INTO @LeadId, @LastName;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @Slug = N'';
            SET @Source = LOWER(LTRIM(RTRIM(ISNULL(@LastName, N''))));
            SET @Index = 1;
            SET @Len = LEN(@Source);

            WHILE @Index <= @Len
            BEGIN
                SET @Ch = SUBSTRING(@Source, @Index, 1);
                IF @Ch LIKE N'[a-z]'
                    SET @Slug += @Ch;

                SET @Index += 1;
            END

            IF LEN(@Slug) = 0
                SET @Slug = N'unknown';

            SET @Suffix = 1;

            WHILE 1 = 1
            BEGIN
                SET @Candidate = CASE
                    WHEN @Suffix = 1 THEN CONCAT(N'no-email@', @Slug, N'.com')
                    ELSE CONCAT(N'no-email@', @Slug, CONVERT(VARCHAR(11), @Suffix), N'.com')
                END;

                IF NOT EXISTS (
                        SELECT 1
                        FROM dbo.WorkstationLeadProfiles
                        WHERE LeadId <> @LeadId
                          AND LOWER(LTRIM(RTRIM(ISNULL(Email, '')))) = LOWER(@Candidate)
                    )
                    AND (
                        OBJECT_ID(N'dbo.ClientProfiles', N'U') IS NULL
                        OR NOT EXISTS (
                            SELECT 1
                            FROM dbo.ClientProfiles
                            WHERE LOWER(LTRIM(RTRIM(ISNULL(Email, '')))) = LOWER(@Candidate)
                               OR LOWER(LTRIM(RTRIM(ISNULL(NormalizedEmail, '')))) = LOWER(@Candidate)
                        )
                    )
                BEGIN
                    BREAK;
                END

                SET @Suffix += 1;
            END

            UPDATE dbo.WorkstationLeadProfiles
            SET Email = @Candidate,
                UpdatedUtc = SYSUTCDATETIME()
            WHERE LeadId = @LeadId;

            SET @UpdatedCount += @@ROWCOUNT;

            FETCH NEXT FROM lead_cursor INTO @LeadId, @LastName;
        END

        CLOSE lead_cursor;
        DEALLOCATE lead_cursor;

        PRINT CONCAT('Legacy CRM placeholder emails updated: ', @UpdatedCount);
    END
END
");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-time data cleanup only; legacy placeholder emails are not restored.
        }
    }
}
