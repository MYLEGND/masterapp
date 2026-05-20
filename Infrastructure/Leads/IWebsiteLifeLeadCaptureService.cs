using System;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Leads;

public interface IWebsiteLifeLeadCaptureService
{
    Task<WebsiteLifeLeadCaptureResult> UpsertAsync(WebsiteLifeLeadCaptureRequest request, CancellationToken cancellationToken = default);
}

public sealed record WebsiteLifeLeadCaptureRequest
{
    public Guid WebsiteLeadId { get; init; }
    public DateTime SubmittedUtc { get; init; }
    public string ProductType { get; init; } = "";
    public string? OfferKey { get; init; }
    public string FirstName { get; init; } = "";
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? State { get; init; }
    public int? Age { get; init; }
    public string? AgeRange { get; init; }
    public int? CoverageAmount { get; init; }
    public string? CoverageAmountOption { get; init; }
    public Guid? AgentTrackingProfileId { get; init; }
    public string? AgentSlug { get; init; }
    public string? RecipientEmail { get; init; }
}

public sealed record WebsiteLifeLeadCaptureResult(
    bool Captured,
    bool Created,
    string? WorkstationLeadId,
    string? Bucket,
    string? AgentUserId,
    string? Reason);
