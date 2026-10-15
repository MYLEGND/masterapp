using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed class PublicBookingControllerParityTests
{
    [Theory]
    [InlineData("LifeQuoteController.cs", "Life/booking-experience", "Life/booking-confirmation")]
    [InlineData("DisabilityQuoteController.cs", "Disability/booking-experience", "Disability/booking-confirmation")]
    [InlineData("DentalVisionHearingQuoteController.cs", "Dental-Vision-Hearing/booking-experience", "Dental-Vision-Hearing/booking-confirmation")]
    public void ProtectQuoteControllersUseTheSameBookingResolverAndConfirmationAuthority(
        string fileName,
        string activationRoute,
        string confirmationRoute)
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "Protect-Website", "Controllers", fileName));

        Assert.Contains("IPublicBookingResolver", source, StringComparison.Ordinal);
        Assert.Contains("IPublicBookingConfirmationService", source, StringComparison.Ordinal);
        Assert.Contains("IPublicBookingContextProtector", source, StringComparison.Ordinal);
        Assert.Contains($"[HttpPost(\"{activationRoute}\")]", source, StringComparison.Ordinal);
        Assert.Contains($"[HttpPost(\"{confirmationRoute}\")]", source, StringComparison.Ordinal);
        Assert.Contains("_publicBookingResolver.ResolveAsync(", source, StringComparison.Ordinal);
        Assert.Contains("_publicBookingConfirmationService.TryConfirmAsync(", source, StringComparison.Ordinal);
        Assert.Contains("_publicBookingContextProtector.TryUnprotect(", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LifeQuoteController.cs")]
    [InlineData("DisabilityQuoteController.cs")]
    [InlineData("DentalVisionHearingQuoteController.cs")]
    public void ProtectQuoteControllersReturnVerifiedBookingTruthFromTheSharedConfirmationResult(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "Protect-Website", "Controllers", fileName));

        Assert.Contains("confirmationVerified = result.Verified", source, StringComparison.Ordinal);
        Assert.Contains("pendingConfirmation = result.PendingConfirmation", source, StringComparison.Ordinal);
        Assert.Contains("calendarEventId = result.CalendarEventId", source, StringComparison.Ordinal);
        Assert.Contains("scheduledStartUtc = result.ScheduledStartUtc", source, StringComparison.Ordinal);
        Assert.Contains("scheduledEndUtc = result.ScheduledEndUtc", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(Path.Combine(workspace, "Protect-Website")))
            return workspace;

        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, "Protect-Website")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
