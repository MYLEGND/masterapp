using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Commerce.Payments;

public sealed record MerchantConnectionView(Guid Id, string Provider, string Environment, string AccountId, string? LocationId, int Revision, string State, MerchantReadiness Readiness);
public sealed record MerchantProviderView(string Provider, bool Available, MerchantCapabilities Capabilities);
public sealed record MerchantConnectionsView(string Environment, Guid? DefaultConnectionId, IReadOnlyList<MerchantConnectionView> Connections, IReadOnlyList<MerchantProviderView> Providers);

public sealed class MerchantConnectionService(MasterAppDbContext db, IDataProtectionProvider protection, IEnumerable<IMerchantPaymentGateway> gateways, Microsoft.Extensions.Configuration.IConfiguration configuration)
{
    private sealed record AuthorizationState(Guid Id, Guid BusinessId, Guid ProfileId, string Actor, string Ticket, string Environment, DateTime ExpiresUtc, string Nonce, string? ExpectedMerchantId);
    private IDataProtector StateProtector => protection.CreateProtector("Legend.Merchant.Authorization.v1");
    private IMerchantPaymentGateway Gateway(string provider) => gateways.SingleOrDefault(x => x.Provider == provider) ?? throw new MerchantPaymentException("unsupported_provider");
    private static string EnvironmentKey(string environment) => environment is "Sandbox" or "Production" ? environment : throw new MerchantPaymentException("invalid_environment");
    private async Task Authorize(Guid businessId, Guid profileId, string actor, string? email, CancellationToken ct)
    {
        if (await BusinessWorkspaceAccess.ResolveAsync(db, businessId, profileId, actor, email, "settings", ct) is null)
            throw new UnauthorizedAccessException("Merchant connection owner access required.");
    }
    private IDataProtector CredentialProtector(Guid id, Guid business, string provider, string environment) =>
        protection.CreateProtector("Legend.Merchant.Credential.v1", id.ToString("N"), business.ToString("N"), provider, environment);
    private static MerchantReadiness Readiness(CommerceMerchantConnection x) => JsonSerializer.Deserialize<MerchantReadiness>(x.ReadinessJson) ?? new(false, false, [], [], [], "Refresh connection");
    public async Task<MerchantConnectionsView> ListAsync(Guid businessId, Guid profileId, string actor, string? email, string environment, CancellationToken ct)
    {
        await Authorize(businessId, profileId, actor, email, ct);
        EnvironmentKey(environment);
        var rows = await db.CommerceMerchantConnections.AsNoTracking().Where(x => x.CommerceBusinessId == businessId && x.Environment == environment).ToListAsync(ct);
        var config = await db.CommerceMerchantConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.CommerceBusinessId == businessId && x.Environment == environment, ct);
        return new(environment, config?.DefaultConnectionId, rows.Select(x => new MerchantConnectionView(x.Id,x.Provider,x.Environment,x.AccountId,x.LocationId,x.Revision,x.State,Readiness(x))).ToArray(), gateways.Select(x => new MerchantProviderView(x.Provider,x.IsAvailable(environment),x.Capabilities)).ToArray());
    }
    public async Task<string> BeginAsync(Guid businessId, Guid profileId, string actor, string? email, string provider, string environment, string callbackUrl, string returnTicket, CancellationToken ct)
    {
        await Authorize(businessId, profileId, actor, email, ct);
        return await BeginAuthorizedAsync(businessId, profileId, actor, provider, environment, callbackUrl, returnTicket, null, ct);
    }
    public async Task<string> ResumeAsync(Guid businessId, Guid profileId, string actor, string? email,
        Guid connectionId, int expectedRevision, string callbackUrl, string returnTicket, CancellationToken ct)
    {
        var connection = await Owned(businessId, profileId, actor, email, connectionId, ct);
        CheckRevision(connection, expectedRevision);
        if (connection.State == "Disconnected") throw new MerchantPaymentException("connection_disconnected");
        return await BeginAuthorizedAsync(businessId, profileId, actor, connection.Provider,
            connection.Environment, callbackUrl, returnTicket, connection.AccountId, ct);
    }
    private async Task<string> BeginAuthorizedAsync(Guid businessId, Guid profileId, string actor,
        string provider, string environment, string callbackUrl, string returnTicket,
        string? existingAccountId, CancellationToken ct)
    {
        EnvironmentKey(environment);
        var gateway = Gateway(provider);
        if (!gateway.IsAvailable(environment)) throw new MerchantPaymentException("provider_not_configured");
        callbackUrl = configuration[$"MerchantPayments:{provider}:{environment}:CallbackUrl"] ?? "";
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/commerce/manage/payments/callback" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new MerchantPaymentException("https_callback_required");
        var authorization = new CommerceMerchantAuthorization { Id=Guid.NewGuid(), CommerceBusinessId=businessId, Provider=provider, Environment=environment, ActorId=actor, ClientProfileId=profileId, ProtectedReturnTicket=returnTicket, CallbackUrl=callbackUrl, ExpiresUtc=DateTime.UtcNow.AddMinutes(10), Version=1 };
        var state = StateProtector.Protect(JsonSerializer.Serialize(new AuthorizationState(authorization.Id,businessId,profileId,actor,returnTicket,environment,authorization.ExpiresUtc,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),existingAccountId)));
        authorization.StateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        db.CommerceMerchantAuthorizations.Add(authorization);
        await db.SaveChangesAsync(ct);
        var result = await gateway.BeginConnectionAsync(new(businessId,environment,state,callbackUrl,existingAccountId),ct);
        authorization.ExpectedAccountId = result.AccountId;
        authorization.Version++;
        await db.SaveChangesAsync(ct);
        return result.RedirectUrl;
    }
    private AuthorizationState ParseState(string state)
    {
        try
        {
            var result = JsonSerializer.Deserialize<AuthorizationState>(StateProtector.Unprotect(state));
            if (result is null || result.ExpiresUtc <= DateTime.UtcNow) throw new MerchantPaymentException("authorization_expired");
            return result;
        }
        catch (CryptographicException) { throw new MerchantPaymentException("invalid_authorization_state"); }
        catch (JsonException) { throw new MerchantPaymentException("invalid_authorization_state"); }
    }
    public string InspectTicket(string state) => ParseState(state).Ticket;
    public string InspectEnvironment(string state) => ParseState(state).Environment;
    public async Task<Guid> CompleteAsync(Guid businessId, Guid profileId, string actor, string? email, string state, string code, CancellationToken ct)
    {
        await Authorize(businessId,profileId,actor,email,ct);
        var parsed = ParseState(state);
        if (parsed.BusinessId != businessId || parsed.ProfileId != profileId || parsed.Actor != actor) throw new MerchantPaymentException("authorization_owner_mismatch");
        var record = await db.CommerceMerchantAuthorizations.SingleOrDefaultAsync(x => x.Id == parsed.Id,ct) ?? throw new MerchantPaymentException("invalid_authorization_state");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        if (record.StateHash != hash || record.CommerceBusinessId != businessId || record.ActorId != actor || record.ConsumedUtc != null || record.ExpiresUtc <= DateTime.UtcNow) throw new MerchantPaymentException("authorization_expired_or_consumed");
        record.ConsumedUtc=DateTime.UtcNow; record.Version++;
        await db.SaveChangesAsync(ct); // Concurrency token consumes once before any provider mutation.
        var gateway = Gateway(record.Provider);
        var result = await gateway.CompleteConnectionAsync(record.Environment,code,record.CallbackUrl,record.ExpectedAccountId,ct);
        if (string.IsNullOrWhiteSpace(result.AccountId)) throw new MerchantPaymentException("merchant_identity_missing");
        if (parsed.ExpectedMerchantId is not null && result.AccountId != parsed.ExpectedMerchantId)
            throw new MerchantPaymentException("merchant_identity_mismatch");
        var readiness = await gateway.GetReadinessAsync(new(record.Provider,record.Environment,result.AccountId,null,result.Credential),ct);
        var connection = new CommerceMerchantConnection { Id=Guid.NewGuid(), CommerceBusinessId=businessId, Provider=record.Provider, Environment=record.Environment, AccountId=result.AccountId, Revision=1, State="PendingConfirmation", GrantedScopesJson=JsonSerializer.Serialize(result.GrantedScopes),ReadinessJson=JsonSerializer.Serialize(readiness),CreatedUtc=DateTime.UtcNow,UpdatedUtc=DateTime.UtcNow };
        connection.ProtectedCredential=CredentialProtector(connection.Id,businessId,connection.Provider,connection.Environment).Protect(JsonSerializer.Serialize(result.Credential));
        var existing = await db.CommerceMerchantConnections.SingleOrDefaultAsync(x=>x.Provider==connection.Provider && x.Environment==connection.Environment && x.AccountId==connection.AccountId,ct);
        if(existing is not null)
        {
            if(existing.CommerceBusinessId!=businessId) throw new MerchantPaymentException("merchant_already_bound");
            existing.ProtectedCredential=CredentialProtector(existing.Id,businessId,existing.Provider,existing.Environment).Protect(JsonSerializer.Serialize(result.Credential));
            existing.ReadinessJson=connection.ReadinessJson; existing.GrantedScopesJson=connection.GrantedScopesJson;
            existing.State="PendingConfirmation"; existing.DisconnectedUtc=null; existing.Revision++; existing.UpdatedUtc=DateTime.UtcNow;
            connection=existing;
        }
        else db.CommerceMerchantConnections.Add(connection);
        Snapshot(connection, actor, "Authorized");
        await db.SaveChangesAsync(ct);
        return connection.Id;
    }
    private async Task<CommerceMerchantConnection> Owned(Guid businessId,Guid profileId,string actor,string? email,Guid id,CancellationToken ct)
    {
        await Authorize(businessId,profileId,actor,email,ct);
        return await db.CommerceMerchantConnections.SingleOrDefaultAsync(x => x.Id==id && x.CommerceBusinessId==businessId,ct) ?? throw new MerchantPaymentException("connection_not_found");
    }
    public async Task ConfirmAsync(Guid businessId,Guid profileId,string actor,string? email,Guid connectionId,string accountId,string? locationId,int expectedRevision,CancellationToken ct)
    {
        var row = await Owned(businessId,profileId,actor,email,connectionId,ct);
        CheckRevision(row,expectedRevision);
        if (row.State != "PendingConfirmation" || row.AccountId != accountId) throw new MerchantPaymentException("merchant_confirmation_mismatch");
        var account = Account(row) with { LocationId = locationId };
        var readiness = await Gateway(row.Provider).GetReadinessAsync(account,ct);
        if (readiness.Locations.Length > 0 && (locationId is null || !readiness.Locations.Contains(locationId,StringComparer.Ordinal))) throw new MerchantPaymentException("select_verified_location");
        if (readiness.Locations.Length == 0 && locationId is not null) throw new MerchantPaymentException("invalid_location");
        row.LocationId=locationId; row.State="Active"; row.Revision++; row.UpdatedUtc=DateTime.UtcNow; row.ReadinessJson=JsonSerializer.Serialize(readiness);
        Snapshot(row, actor, "Confirmed"); await db.SaveChangesAsync(ct);
    }
    public async Task SetDefaultAsync(Guid businessId,Guid profileId,string actor,string? email,Guid connectionId,int expectedRevision,CancellationToken ct)
    {
        var row=await Owned(businessId,profileId,actor,email,connectionId,ct);
        CheckRevision(row,expectedRevision);
        if (row.State != "Active") throw new MerchantPaymentException("connection_not_active");
        var readiness=await Gateway(row.Provider).GetReadinessAsync(Account(row),ct);
        if (!readiness.PaymentsEnabled || !readiness.SettlementsEnabled || !Gateway(row.Provider).Capabilities.HasFlag(MerchantCapabilities.Checkout)) throw new MerchantPaymentException("merchant_not_ready");
        var configuration=await db.CommerceMerchantConfigurations.SingleOrDefaultAsync(x=>x.CommerceBusinessId==businessId && x.Environment==row.Environment,ct);
        if (configuration is null) { configuration=new() { Id=Guid.NewGuid(),CommerceBusinessId=businessId,Environment=row.Environment }; db.CommerceMerchantConfigurations.Add(configuration); }
        configuration.DefaultConnectionId=row.Id; configuration.Version++;
        row.ReadinessJson=JsonSerializer.Serialize(readiness); row.UpdatedUtc=DateTime.UtcNow; row.Revision++; Snapshot(row, actor, "DefaultSelected");
        await db.SaveChangesAsync(ct);
    }
    public async Task RevokeAsync(Guid businessId,Guid profileId,string actor,string? email,Guid connectionId,int expectedRevision,CancellationToken ct)
    {
        var row=await Owned(businessId,profileId,actor,email,connectionId,ct);
        CheckRevision(row,expectedRevision);
        if(row.State=="Disconnected") return;
        // Provider access is retained for historical refunds/reconciliation. This disables future routing.
        // A remote revocation must be initiated at the provider after all outstanding obligations settle.
        var configuration=await db.CommerceMerchantConfigurations.SingleOrDefaultAsync(x=>x.CommerceBusinessId==businessId && x.Environment==row.Environment,ct);
        if(configuration?.DefaultConnectionId==row.Id) { configuration.DefaultConnectionId=null; configuration.Version++; }
        row.State="Disconnected"; row.DisconnectedUtc=DateTime.UtcNow; row.UpdatedUtc=DateTime.UtcNow; row.Revision++;
        Snapshot(row, actor, "Disconnected"); await db.SaveChangesAsync(ct);
    }
    private static void CheckRevision(CommerceMerchantConnection row,int expectedRevision)
    {
        if(row.Revision!=expectedRevision) throw new MerchantPaymentException("connection_revision_changed");
    }
    public async Task RefreshAsync(Guid businessId,Guid profileId,string actor,string? email,Guid connectionId,int expectedRevision,CancellationToken ct)
    {
        var row=await Owned(businessId,profileId,actor,email,connectionId,ct);
        CheckRevision(row,expectedRevision);
        if(row.State=="Disconnected") throw new MerchantPaymentException("connection_disconnected");
        var account=Account(row);
        var credential=await Gateway(row.Provider).RefreshAsync(account,ct);
        var readiness=await Gateway(row.Provider).GetReadinessAsync(account with { Credential=credential },ct);
        row.ProtectedCredential=CredentialProtector(row.Id,row.CommerceBusinessId,row.Provider,row.Environment).Protect(JsonSerializer.Serialize(credential));
        row.ReadinessJson=JsonSerializer.Serialize(readiness); row.Revision++; row.UpdatedUtc=DateTime.UtcNow;
        Snapshot(row, actor, "Refreshed"); await db.SaveChangesAsync(ct);
    }
    public async Task<CommerceMerchantConnection?> ResolveActiveAsync(Guid businessId,string environment,CancellationToken ct)
    {
        EnvironmentKey(environment);
        var id=await db.CommerceMerchantConfigurations.Where(x=>x.CommerceBusinessId==businessId && x.Environment==environment).Select(x=>x.DefaultConnectionId).SingleOrDefaultAsync(ct);
        return id is null ? null : await db.CommerceMerchantConnections.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id && x.CommerceBusinessId==businessId && x.Environment==environment && x.State=="Active",ct);
    }
    private MerchantAccount Account(CommerceMerchantConnection row) => new(row.Provider,row.Environment,row.AccountId,row.LocationId,JsonSerializer.Deserialize<MerchantCredential>(CredentialProtector(row.Id,row.CommerceBusinessId,row.Provider,row.Environment).Unprotect(row.ProtectedCredential)) ?? throw new MerchantPaymentException("credential_unavailable"));
    public async Task<MerchantAccount> LoadAccountAsync(CommerceMerchantConnectionRevision revision,CancellationToken ct)
    {
        // Credentials may rotate, but settlement remains pinned to the original merchant and location.
        var row=await db.CommerceMerchantConnections.SingleOrDefaultAsync(x=>x.Id==revision.ConnectionId && x.CommerceBusinessId==revision.CommerceBusinessId && x.Provider==revision.Provider && x.Environment==revision.Environment && x.AccountId==revision.AccountId,ct) ?? throw new MerchantPaymentException("historical_merchant_unavailable");
        var account=Account(row);
        if(account.Credential.ExpiresUtc is DateTime expiry && expiry<=DateTime.UtcNow.AddMinutes(2))
        {
            var refreshed=await Gateway(row.Provider).RefreshAsync(account,ct);
            row.ProtectedCredential=CredentialProtector(row.Id,row.CommerceBusinessId,row.Provider,row.Environment).Protect(JsonSerializer.Serialize(refreshed));
            row.Revision++; row.UpdatedUtc=DateTime.UtcNow; Snapshot(row, "system", "CredentialRotated");
            await db.SaveChangesAsync(ct);
            account=account with { Credential=refreshed };
        }
        return account with { LocationId=revision.LocationId };
    }
    private void Snapshot(CommerceMerchantConnection row, string actor, string action) => db.CommerceMerchantConnectionRevisions.Add(new() {Id=Guid.NewGuid(),ConnectionId=row.Id,CommerceBusinessId=row.CommerceBusinessId,Provider=row.Provider,Environment=row.Environment,AccountId=row.AccountId,LocationId=row.LocationId,Revision=row.Revision,ProtectedCredential=row.ProtectedCredential,State=row.State,ActorId=actor,Action=action,GrantedScopesJson=row.GrantedScopesJson,ReadinessJson=row.ReadinessJson,CreatedUtc=DateTime.UtcNow});
}
