using Domain.Entities.FinancialIntelligence;
using Domain.FinancialIntelligence;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.FinancialIntelligence;

internal sealed class FinancialConnectionService : IFinancialConnectionService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<FinancialConnectionService> _logger;

    public FinancialConnectionService(
        MasterAppDbContext db,
        ILogger<FinancialConnectionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<FinancialDataConnectionResult> GetAsync(
        Guid clientProfileId,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        if (clientProfileId == Guid.Empty || connectionId == Guid.Empty)
            return Failure("CONNECTION_SCOPE_INVALID", "A client profile and connection are required.");

        var connection = await FinancialIntelligenceScope.FindConnectionAsync(
            _db,
            clientProfileId,
            connectionId,
            asNoTracking: true,
            cancellationToken);

        return connection is null
            ? Failure("CONNECTION_NOT_FOUND", "The requested financial connection was not found for this client.")
            : Success(connection);
    }

    public async Task<IReadOnlyList<FinancialDataConnection>> ListAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        if (clientProfileId == Guid.Empty)
            return Array.Empty<FinancialDataConnection>();

        return await _db.FinancialDataConnections
            .AsNoTracking()
            .Where(x => x.ClientProfileId == clientProfileId)
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public async Task<FinancialDataConnectionResult> UpsertAsync(
        FinancialDataConnectionUpsertCommand command,
        CancellationToken cancellationToken = default)
    {
        var providerKey = NormalizeRequired(command.ProviderKey);
        var providerItemId = NormalizeRequired(command.ProviderItemId);
        if (command.ClientProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(providerKey) ||
            string.IsNullOrWhiteSpace(providerItemId))
        {
            return Failure("CONNECTION_INPUT_INVALID", "A client profile, provider key, and provider item id are required.");
        }

        if (!Fits(providerKey, 50) || !Fits(providerItemId, 200) ||
            !Fits(command.ProviderInstitutionId, 200) || !Fits(command.DisplayName, 200) ||
            !Fits(command.EncryptedAccessToken, 4000) || !Fits(command.Status, 40))
        {
            return Failure("CONNECTION_INPUT_INVALID", "One or more financial connection values exceed the supported length.");
        }

        if (!await FinancialIntelligenceScope.ClientProfileExistsAsync(_db, command.ClientProfileId, cancellationToken))
            return Failure("CLIENT_PROFILE_NOT_FOUND", "The requested client profile was not found.");

        FinancialDataConnection? connection;
        if (command.ConnectionId.HasValue && command.ConnectionId.Value != Guid.Empty)
        {
            connection = await FinancialIntelligenceScope.FindConnectionAsync(
                _db,
                command.ClientProfileId,
                command.ConnectionId.Value,
                asNoTracking: false,
                cancellationToken);

            if (connection is null)
                return Failure("CONNECTION_NOT_FOUND", "The requested financial connection was not found for this client.");
        }
        else
        {
            connection = await _db.FinancialDataConnections.FirstOrDefaultAsync(
                x => x.ClientProfileId == command.ClientProfileId &&
                     x.ProviderKey == providerKey &&
                     x.ProviderItemId == providerItemId,
                cancellationToken);
        }

        var nowUtc = DateTime.UtcNow;
        var isNew = connection is null;
        if (isNew)
        {
            connection = new FinancialDataConnection
            {
                ClientProfileId = command.ClientProfileId,
                ProviderKey = providerKey,
                ProviderItemId = providerItemId,
                CreatedUtc = nowUtc
            };
            _db.FinancialDataConnections.Add(connection);
        }

        var trackedConnection = connection!;
        trackedConnection.ProviderKey = providerKey;
        trackedConnection.ProviderItemId = providerItemId;
        trackedConnection.ProviderInstitutionId = NormalizeOptional(command.ProviderInstitutionId);
        trackedConnection.DisplayName = NormalizeOptional(command.DisplayName);
        trackedConnection.Status = NormalizeOptional(command.Status) ?? trackedConnection.Status;
        if (string.IsNullOrWhiteSpace(trackedConnection.Status))
            trackedConnection.Status = "Active";

        if (command.EncryptedAccessToken is not null)
            trackedConnection.EncryptedAccessToken = command.EncryptedAccessToken;

        trackedConnection.UpdatedUtc = nowUtc;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(
                ex,
                "Financial connection save failed. ClientProfileId={ClientProfileId} ProviderKey={ProviderKey}",
                command.ClientProfileId,
                providerKey);
            return Failure("CONNECTION_SAVE_FAILED", "The financial connection could not be saved.");
        }

        _logger.LogInformation(
            "Financial connection {Operation}. ClientProfileId={ClientProfileId} ConnectionId={ConnectionId} ProviderKey={ProviderKey}",
            isNew ? "created" : "updated",
            command.ClientProfileId,
            trackedConnection.Id,
            trackedConnection.ProviderKey);

        return Success(trackedConnection);
    }

    public async Task<FinancialDataConnectionResult> UpdateStatusAsync(
        FinancialDataConnectionStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        var status = NormalizeRequired(command.Status);
        if (command.ClientProfileId == Guid.Empty || command.ConnectionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(status) || !Fits(status, 40) ||
            !Fits(command.LastErrorCode, 100) || !Fits(command.LastErrorMessage, 2000))
        {
            return Failure("CONNECTION_STATUS_INVALID", "The financial connection status update is invalid.");
        }

        var connection = await FinancialIntelligenceScope.FindConnectionAsync(
            _db,
            command.ClientProfileId,
            command.ConnectionId,
            asNoTracking: false,
            cancellationToken);

        if (connection is null)
            return Failure("CONNECTION_NOT_FOUND", "The requested financial connection was not found for this client.");

        connection.Status = status;
        connection.LastErrorCode = NormalizeOptional(command.LastErrorCode);
        connection.LastErrorMessage = NormalizeOptional(command.LastErrorMessage);
        connection.UpdatedUtc = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(
                ex,
                "Financial connection status save failed. ClientProfileId={ClientProfileId} ConnectionId={ConnectionId}",
                command.ClientProfileId,
                command.ConnectionId);
            return Failure("CONNECTION_SAVE_FAILED", "The financial connection status could not be saved.");
        }

        return Success(connection);
    }

    private static FinancialDataConnectionResult Success(FinancialDataConnection connection) =>
        new(true, null, null, connection);

    private static FinancialDataConnectionResult Failure(string errorCode, string summary) =>
        new(false, errorCode, summary);

    private static bool Fits(string? value, int maximumLength) =>
        value is null || value.Trim().Length <= maximumLength;

    private static string NormalizeRequired(string? value) => value?.Trim() ?? string.Empty;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
