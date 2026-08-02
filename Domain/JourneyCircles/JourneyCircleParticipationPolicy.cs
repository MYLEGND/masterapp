using System.Linq.Expressions;
using Domain.Entities;

namespace Domain.JourneyCircles;

/// <summary>
/// The one authority for Journey Circles participation eligibility.
///
/// One community-participation confirmation starts matching. Individual
/// visibility, recommendation, and connection preferences still govern their
/// respective capabilities; they are not stacked into a second join requirement.
/// </summary>
public static class JourneyCircleParticipationPolicy
{
    public static readonly Expression<Func<JourneyCircleProfile, bool>>
        RecommendationCandidateExpression = profile =>
            profile.CommunityAccessState == "Active" &&
            profile.ConsentAffirmedUtc != null &&
            profile.IsDiscoverable;

    public static bool HasAtLeastOneResponse(JourneyCircleProfileInput input) =>
        HasAtLeastOneResponse(
            input.ConsentAffirmed,
            input.IsOptedIn,
            input.IsDiscoverable,
            input.AllowSuggestions,
            input.AllowConnectionRequests);

    public static bool IsEligibleForMatching(JourneyCircleProfile? profile) =>
        profile is not null &&
        profile.CommunityAccessState == "Active" &&
        profile.ConsentAffirmedUtc is not null &&
        HasAtLeastOneResponse(
            profile.ConsentAffirmedUtc is not null,
            profile.IsOptedIn,
            profile.IsDiscoverable,
            profile.AllowSuggestions,
            profile.AllowConnectionRequests);

    private static bool HasAtLeastOneResponse(
        bool consentAffirmed,
        bool isOptedIn,
        bool isDiscoverable,
        bool allowSuggestions,
        bool allowConnectionRequests) =>
        consentAffirmed ||
        isOptedIn ||
        isDiscoverable ||
        allowSuggestions ||
        allowConnectionRequests;
}
