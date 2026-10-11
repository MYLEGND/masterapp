BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    IF EXISTS (SELECT 1 FROM [CommerceOrders] WHERE [CheckoutAttemptId] IS NOT NULL
        GROUP BY [CommerceBusinessId], [CheckoutAttemptId] HAVING COUNT_BIG(*) > 1)
        THROW 51001, 'MERCHANT_CHECKOUT_DUPLICATE_IDENTITY_REQUIRES_REVIEW', 1;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    ALTER TABLE [CommerceOrders] ADD [PaymentEnvironment] nvarchar(40) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    ALTER TABLE [CommerceOrders] ADD [PaymentProvider] nvarchar(40) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    ALTER TABLE [CommerceOrders] ADD [PaymentReference] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE TABLE [CommerceMerchantAuthorizations] (
        [Id] uniqueidentifier NOT NULL,
        [CommerceBusinessId] uniqueidentifier NOT NULL,
        [ClientProfileId] uniqueidentifier NOT NULL,
        [ProtectedReturnTicket] nvarchar(max) NOT NULL,
        [Provider] nvarchar(32) NOT NULL,
        [Environment] nvarchar(16) NOT NULL,
        [ActorId] nvarchar(200) NOT NULL,
        [StateHash] nvarchar(100) NOT NULL,
        [CallbackUrl] nvarchar(2000) NOT NULL,
        [ExpectedAccountId] nvarchar(200) NULL,
        [ExpiresUtc] datetime2 NOT NULL,
        [ConsumedUtc] datetime2 NULL,
        [Version] int NOT NULL,
        CONSTRAINT [PK_CommerceMerchantAuthorizations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CommerceMerchantAuthorizations_CommerceBusinesses_CommerceBusinessId] FOREIGN KEY ([CommerceBusinessId]) REFERENCES [CommerceBusinesses] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE TABLE [CommerceMerchantConnections] (
        [Id] uniqueidentifier NOT NULL,
        [CommerceBusinessId] uniqueidentifier NOT NULL,
        [Provider] nvarchar(32) NOT NULL,
        [Environment] nvarchar(16) NOT NULL,
        [AccountId] nvarchar(200) NOT NULL,
        [LocationId] nvarchar(100) NULL,
        [Revision] int NOT NULL,
        [ProtectedCredential] nvarchar(max) NOT NULL,
        [GrantedScopesJson] nvarchar(max) NOT NULL,
        [ReadinessJson] nvarchar(max) NOT NULL,
        [State] nvarchar(100) NOT NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [UpdatedUtc] datetime2 NOT NULL,
        [DisconnectedUtc] datetime2 NULL,
        CONSTRAINT [PK_CommerceMerchantConnections] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CommerceMerchantConnections_CommerceBusinesses_CommerceBusinessId] FOREIGN KEY ([CommerceBusinessId]) REFERENCES [CommerceBusinesses] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE TABLE [CommerceMerchantConfigurations] (
        [Id] uniqueidentifier NOT NULL,
        [CommerceBusinessId] uniqueidentifier NOT NULL,
        [Environment] nvarchar(16) NOT NULL,
        [DefaultConnectionId] uniqueidentifier NULL,
        [Version] int NOT NULL,
        CONSTRAINT [PK_CommerceMerchantConfigurations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CommerceMerchantConfigurations_CommerceBusinesses_CommerceBusinessId] FOREIGN KEY ([CommerceBusinessId]) REFERENCES [CommerceBusinesses] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CommerceMerchantConfigurations_CommerceMerchantConnections_DefaultConnectionId] FOREIGN KEY ([DefaultConnectionId]) REFERENCES [CommerceMerchantConnections] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE TABLE [CommerceMerchantConnectionRevisions] (
        [Id] uniqueidentifier NOT NULL,
        [ConnectionId] uniqueidentifier NOT NULL,
        [CommerceBusinessId] uniqueidentifier NOT NULL,
        [Provider] nvarchar(32) NOT NULL,
        [Environment] nvarchar(16) NOT NULL,
        [AccountId] nvarchar(200) NOT NULL,
        [LocationId] nvarchar(100) NULL,
        [Revision] int NOT NULL,
        [Action] nvarchar(40) NOT NULL,
        [ActorId] nvarchar(200) NOT NULL,
        [GrantedScopesJson] nvarchar(max) NOT NULL,
        [ReadinessJson] nvarchar(max) NOT NULL,
        [State] nvarchar(40) NOT NULL,
        [ProtectedCredential] nvarchar(max) NOT NULL,
        [CreatedUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_CommerceMerchantConnectionRevisions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CommerceMerchantConnectionRevisions_CommerceMerchantConnections_ConnectionId] FOREIGN KEY ([ConnectionId]) REFERENCES [CommerceMerchantConnections] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE TABLE [CommercePaymentAttempts] (
        [Id] uniqueidentifier NOT NULL,
        [CommerceBusinessId] uniqueidentifier NOT NULL,
        [CommerceOrderId] uniqueidentifier NOT NULL,
        [ConnectionId] uniqueidentifier NOT NULL,
        [ConnectionRevisionId] uniqueidentifier NOT NULL,
        [Provider] nvarchar(32) NOT NULL,
        [Environment] nvarchar(16) NOT NULL,
        [AccountId] nvarchar(200) NOT NULL,
        [LocationId] nvarchar(100) NULL,
        [IdempotencyKey] nvarchar(200) NOT NULL,
        [AmountMinor] bigint NOT NULL,
        [Currency] nvarchar(100) NOT NULL,
        [State] nvarchar(100) NOT NULL,
        [ProviderReference] nvarchar(200) NULL,
        [PaymentReference] nvarchar(200) NULL,
        [CheckoutUrl] nvarchar(2000) NULL,
        [RefundedMinor] bigint NOT NULL,
        [DisputeState] nvarchar(100) NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [UpdatedUtc] datetime2 NOT NULL,
        [CaptureRequestedUtc] datetime2 NULL,
        [SignalContextJson] nvarchar(max) NULL,
        [Version] int NOT NULL,
        CONSTRAINT [PK_CommercePaymentAttempts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CommercePaymentAttempts_CommerceMerchantConnectionRevisions_ConnectionRevisionId] FOREIGN KEY ([ConnectionRevisionId]) REFERENCES [CommerceMerchantConnectionRevisions] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CommercePaymentAttempts_CommerceOrders_CommerceOrderId] FOREIGN KEY ([CommerceOrderId]) REFERENCES [CommerceOrders] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE TABLE [CommercePaymentEffects] (
        [Id] uniqueidentifier NOT NULL,
        [AttemptId] uniqueidentifier NOT NULL,
        [Kind] nvarchar(100) NOT NULL,
        [State] nvarchar(100) NOT NULL,
        [RetryCount] int NOT NULL,
        [SafeErrorCode] nvarchar(100) NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [CompletedUtc] datetime2 NULL,
        [Version] int NOT NULL,
        CONSTRAINT [PK_CommercePaymentEffects] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CommercePaymentEffects_CommercePaymentAttempts_AttemptId] FOREIGN KEY ([AttemptId]) REFERENCES [CommercePaymentAttempts] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE TABLE [CommercePaymentReceipts] (
        [Id] uniqueidentifier NOT NULL,
        [AttemptId] uniqueidentifier NOT NULL,
        [Provider] nvarchar(32) NOT NULL,
        [Environment] nvarchar(16) NOT NULL,
        [AccountId] nvarchar(200) NOT NULL,
        [EventId] nvarchar(200) NOT NULL,
        [State] nvarchar(100) NOT NULL,
        [RetryCount] int NOT NULL,
        [SafeErrorCode] nvarchar(100) NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [ProcessedUtc] datetime2 NULL,
        CONSTRAINT [PK_CommercePaymentReceipts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CommercePaymentReceipts_CommercePaymentAttempts_AttemptId] FOREIGN KEY ([AttemptId]) REFERENCES [CommercePaymentAttempts] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE TABLE [CommercePaymentRefunds] (
        [Id] uniqueidentifier NOT NULL,
        [AttemptId] uniqueidentifier NOT NULL,
        [IdempotencyKey] nvarchar(200) NOT NULL,
        [AmountMinor] bigint NOT NULL,
        [State] nvarchar(100) NOT NULL,
        [ProviderReference] nvarchar(200) NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [Version] int NOT NULL,
        CONSTRAINT [PK_CommercePaymentRefunds] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CommercePaymentRefunds_CommercePaymentAttempts_AttemptId] FOREIGN KEY ([AttemptId]) REFERENCES [CommercePaymentAttempts] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_CommerceOrders_CommerceBusinessId_CheckoutAttemptId] ON [CommerceOrders] ([CommerceBusinessId], [CheckoutAttemptId]) WHERE [CheckoutAttemptId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE INDEX [IX_CommerceMerchantAuthorizations_CommerceBusinessId] ON [CommerceMerchantAuthorizations] ([CommerceBusinessId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CommerceMerchantAuthorizations_StateHash] ON [CommerceMerchantAuthorizations] ([StateHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CommerceMerchantConfigurations_CommerceBusinessId_Environment] ON [CommerceMerchantConfigurations] ([CommerceBusinessId], [Environment]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE INDEX [IX_CommerceMerchantConfigurations_DefaultConnectionId] ON [CommerceMerchantConfigurations] ([DefaultConnectionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CommerceMerchantConnectionRevisions_ConnectionId_Revision] ON [CommerceMerchantConnectionRevisions] ([ConnectionId], [Revision]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE INDEX [IX_CommerceMerchantConnections_CommerceBusinessId] ON [CommerceMerchantConnections] ([CommerceBusinessId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CommerceMerchantConnections_Provider_Environment_AccountId] ON [CommerceMerchantConnections] ([Provider], [Environment], [AccountId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CommercePaymentAttempts_CommerceOrderId] ON [CommercePaymentAttempts] ([CommerceOrderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE INDEX [IX_CommercePaymentAttempts_ConnectionRevisionId] ON [CommercePaymentAttempts] ([ConnectionRevisionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CommercePaymentAttempts_Provider_Environment_AccountId_IdempotencyKey] ON [CommercePaymentAttempts] ([Provider], [Environment], [AccountId], [IdempotencyKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CommercePaymentEffects_AttemptId_Kind] ON [CommercePaymentEffects] ([AttemptId], [Kind]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE INDEX [IX_CommercePaymentReceipts_AttemptId] ON [CommercePaymentReceipts] ([AttemptId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CommercePaymentReceipts_Provider_Environment_AccountId_EventId] ON [CommercePaymentReceipts] ([Provider], [Environment], [AccountId], [EventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CommercePaymentRefunds_AttemptId_IdempotencyKey] ON [CommercePaymentRefunds] ([AttemptId], [IdempotencyKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261011064911_AddBusinessScopedMerchantPayments'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261011064911_AddBusinessScopedMerchantPayments', N'10.0.2');
END;

COMMIT;
GO
