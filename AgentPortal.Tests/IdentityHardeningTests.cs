using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Stage 1 Identity Hardening protection tests.
/// Verifies that NormalizedEmail is populated on every write path
/// and that duplicate-identity guards function correctly.
/// </summary>
public class IdentityHardeningTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static MasterAppDbContext BuildDb() => ControllerTestHelpers.BuildDb();

    private static AgentRegistryService BuildRegistry(MasterAppDbContext db)
    {
        var tracking = Mock.Of<IAgentTrackingService>();
        return new AgentRegistryService(db, NullLogger<AgentRegistryService>.Instance, tracking);
    }

    private static AgentTrackingService BuildTrackingService(MasterAppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Protect:PublicBaseUrl", "https://protect.mylegnd.com"),
                new KeyValuePair<string, string?>("Founder:Upn", "zac.owen@mylegnd.com")
            })
            .Build();
        return new AgentTrackingService(db, NullLogger<AgentTrackingService>.Instance, config);
    }

    private static ClientProvisioningService BuildProvisioning(MasterAppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("GraphProvisioning:TenantId", "test-tenant"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientId", "test-client"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientSecret", "test-secret")
            })
            .Build();
        return new ClientProvisioningService(config, NullLogger<ClientProvisioningService>.Instance, db);
    }

    private static ClaimsPrincipal BuildUserWithEmail(string oid, string email)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("oid", oid),
            new Claim("preferred_username", email)
        }, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static AssistantPanelController BuildAssistantController(MasterAppDbContext db, string agentOid)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("GraphProvisioning:TenantId", "test-tenant"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientId", "test-client"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientSecret", "test-secret")
            })
            .Build();
        var provisioning = new ClientProvisioningService(config, NullLogger<ClientProvisioningService>.Instance, db);
        var http = new DefaultHttpContext();
        http.Items["EffectiveAgentOid"] = agentOid;
        var tempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        var controller = new AssistantPanelController(db, provisioning, NullLogger<AssistantPanelController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = tempData
        };
        return controller;
    }

    // -----------------------------------------------------------------------
    // 1. AgentRegistryService_UpsertSetsNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentRegistryService_UpsertSetsNormalizedEmail()
    {
        using var db = BuildDb();
        var registry = BuildRegistry(db);
        var user = BuildUserWithEmail("oid-agent-1", "  Test.Agent@EXAMPLE.COM  ");

        await registry.UpsertAgentProfileAsync(user);

        var profile = await db.AgentProfiles.SingleAsync(x => x.AgentUserId == "oid-agent-1");
        Assert.Equal("test.agent@example.com", profile.NormalizedEmail);
    }

    // -----------------------------------------------------------------------
    // 2. AgentRegistryService_UpsertDoesNotCreateDuplicate
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentRegistryService_UpsertDoesNotCreateDuplicate()
    {
        using var db = BuildDb();
        var registry = BuildRegistry(db);
        var user = BuildUserWithEmail("oid-agent-2", "agent2@example.com");

        await registry.UpsertAgentProfileAsync(user);
        await registry.UpsertAgentProfileAsync(user);

        var count = await db.AgentProfiles.CountAsync(x => x.AgentUserId == "oid-agent-2");
        Assert.Equal(1, count);
    }

    // -----------------------------------------------------------------------
    // 2b. AgentRegistryService_ReusesExistingEmailMatchedProfile
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentRegistryService_ReusesExistingEmailMatchedProfile()
    {
        using var db = BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "legacy-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            NormalizedEmail = "zac.owen@mylegnd.com",
            FullName = "Zac Owen",
            CreatedUtc = DateTime.UtcNow.AddDays(-3),
            UpdatedUtc = DateTime.UtcNow.AddDays(-3)
        });
        await db.SaveChangesAsync();

        var registry = BuildRegistry(db);
        var user = BuildUserWithEmail("current-oid", "  Zac.Owen@MyLegnd.com  ");

        await registry.UpsertAgentProfileAsync(user);

        var profiles = await db.AgentProfiles
            .Where(x => x.NormalizedEmail == "zac.owen@mylegnd.com")
            .ToListAsync();

        Assert.Single(profiles);
        Assert.Equal("legacy-oid", profiles[0].AgentUserId);
        Assert.Equal("Zac Owen", profiles[0].FullName);
    }

    // -----------------------------------------------------------------------
    // 2c. AgentRegistryService_BackfillsMissingDataFromDuplicateSibling
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentRegistryService_BackfillsMissingDataFromDuplicateSibling()
    {
        using var db = BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "current-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            NormalizedEmail = "zac.owen@mylegnd.com",
            FullName = "Zac Owen",
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedUtc = DateTime.UtcNow.AddDays(-1)
        });
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "legacy-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            FullName = "Zac Owen",
            BookingEnabled = true,
            MicrosoftBookingsEmbedUrl = "https://outlook.office.com/book/LEGEND@mylegnd.com/",
            FallbackBookingUrl = "https://outlook.office.com/book/LEGEND@mylegnd.com/",
            CalendarEmail = "zac.owen@mylegnd.com",
            CreatedUtc = DateTime.UtcNow.AddDays(-4),
            UpdatedUtc = DateTime.UtcNow.AddDays(-4)
        });
        await db.SaveChangesAsync();

        var registry = BuildRegistry(db);
        var user = BuildUserWithEmail("current-oid", "zac.owen@mylegnd.com");

        await registry.UpsertAgentProfileAsync(user);

        var current = await db.AgentProfiles.SingleAsync(x => x.AgentUserId == "current-oid");
        Assert.True(current.BookingEnabled);
        Assert.Equal("https://outlook.office.com/book/LEGEND@mylegnd.com/", current.MicrosoftBookingsEmbedUrl);
        Assert.Equal("zac.owen@mylegnd.com", current.CalendarEmail);
    }

    // -----------------------------------------------------------------------
    // 2d. AgentTrackingService_ReusesExistingUpnMatchedProfile
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AgentTrackingService_ReusesExistingUpnMatchedProfile()
    {
        using var db = BuildDb();
        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            AgentUserId = "legacy-oid",
            AgentUpn = "zac.owen@mylegnd.com",
            Slug = "zac-owen",
            DisplayName = "Zac Owen",
            CreatedUtc = DateTime.UtcNow.AddDays(-4),
            UpdatedUtc = DateTime.UtcNow.AddDays(-4)
        });
        await db.SaveChangesAsync();

        var tracking = BuildTrackingService(db);
        var profile = await tracking.EnsureProfileAsync("current-oid", "Zac.Owen@MyLegnd.com", "Zac Owen");

        Assert.Equal("zac-owen", profile.Slug);
        Assert.Equal("legacy-oid", profile.AgentUserId);
        Assert.Equal(1, await db.AgentTrackingProfiles.CountAsync());
    }

    // -----------------------------------------------------------------------
    // 2e. AccountController_ManageProfileCreatesNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public void AccountController_ManageProfileCreatesNormalizedEmail()
    {
        using var db = BuildDb();
        var controller = new AccountController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildUserWithEmail("oid-agent-3", "  Zac.Owen@MyLegnd.com  ")
                }
            }
        };

        var result = controller.ManageProfile();

        Assert.IsType<ViewResult>(result);
        var profile = db.AgentProfiles.Single(x => x.AgentUserId == "oid-agent-3");
        Assert.Equal("zac.owen@mylegnd.com", profile.NormalizedEmail);
    }

    // -----------------------------------------------------------------------
    // 3. ClientProvisioningService_EnsureProfileSetsNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ClientProvisioningService_EnsureProfileSetsNormalizedEmail()
    {
        using var db = BuildDb();

        // Seed an AgentClient so the FK constraint is satisfied if present;
        // EnsureClientProfileAndLinkAsync creates both the profile and the link.
        var provisioning = BuildProvisioning(db);

        await provisioning.EnsureClientProfileAndLinkAsync(
            agentUserId: "agent-oid-1",
            clientUserId: "client-oid-1",
            firstName: "Jane",
            lastName: "Smith",
            email: "  Jane.Smith@EXAMPLE.COM  ",
            phone: null, dob: null, maritalStatus: null,
            soFirstName: null, soLastName: null, soDob: null, soEmail: null, soPhone: null);

        var profile = await db.ClientProfiles.SingleAsync(x => x.ClientUserId == "client-oid-1");
        Assert.Equal("jane.smith@example.com", profile.NormalizedEmail);
    }

    // -----------------------------------------------------------------------
    // 4. ClientProvisioningService_EnsureProfileBlocksDuplicateEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ClientProvisioningService_EnsureProfileBlocksDuplicateEmail()
    {
        using var db = BuildDb();
        var provisioning = BuildProvisioning(db);

        // First provisioning — succeeds.
        await provisioning.EnsureClientProfileAndLinkAsync(
            agentUserId: "agent-oid-1",
            clientUserId: "client-oid-A",
            firstName: "Alice",
            lastName: "Jones",
            email: "alice@example.com",
            phone: null, dob: null, maritalStatus: null,
            soFirstName: null, soLastName: null, soDob: null, soEmail: null, soPhone: null);

        // Second provisioning — different ClientUserId, same email — must throw.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioning.EnsureClientProfileAndLinkAsync(
                agentUserId: "agent-oid-2",
                clientUserId: "client-oid-B",
                firstName: "Alice2",
                lastName: "Jones2",
                email: "alice@example.com",
                phone: null, dob: null, maritalStatus: null,
                soFirstName: null, soLastName: null, soDob: null, soEmail: null, soPhone: null));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // 5. AssistantPanelController_CreateSetsNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AssistantPanelController_CreateSetsNormalizedEmail()
    {
        using var db = BuildDb();
        var controller = BuildAssistantController(db, "agent-oid-1");

        var model = new AgentPortal.Models.CreateAssistantViewModel
        {
            FirstName = "Sam",
            LastName = "Helper",
            Email = "  Sam.HELPER@Example.com  "
        };

        // Graph call will fail (fake credentials) but is in a try/catch; the DB record is
        // written before the invite attempt, so it is present regardless.
        await controller.Create(model);

        var assistant = await db.AgentAssistants
            .FirstOrDefaultAsync(a => a.ParentAgentUserId == "agent-oid-1");

        Assert.NotNull(assistant);
        Assert.Equal("sam.helper@example.com", assistant!.NormalizedEmail);
        Assert.Equal("sam.helper@example.com", assistant.Email);
    }

    // -----------------------------------------------------------------------
    // 6. AssistantContextService_BindByNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AssistantContextService_BindByNormalizedEmail()
    {
        using var db = BuildDb();

        // Seed an unbound assistant record with NormalizedEmail set.
        db.AgentAssistants.Add(new AgentAssistant
        {
            Id = Guid.NewGuid(),
            ParentAgentUserId = "parent-agent-oid",
            AssistantUserId = null,           // not yet bound
            FirstName = "Kim",
            LastName = "Assist",
            Email = "kim@example.com",
            NormalizedEmail = "kim@example.com",
            IsActive = true,
            InvitedAt = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new AssistantContextService(db, NullLogger<AssistantContextService>.Instance);

        // Simulate Kim's first login — OID comes in via claims, email matches NormalizedEmail.
        var user = BuildUserWithEmail("kim-oid-123", "KIM@example.com");

        var result = await service.BindAssistantOidIfNeededAsync(user);

        Assert.NotNull(result);
        Assert.Equal("kim-oid-123", result!.AssistantUserId);
    }

    // -----------------------------------------------------------------------
    // 7. OnboardingController_CreateInviteSetsNormalizedEmail
    // -----------------------------------------------------------------------
    [Fact]
    public async Task OnboardingController_CreateInviteSetsNormalizedEmail()
    {
        using var db = BuildDb();
        var emailSender = Mock.Of<IEmailSender>(s =>
            s.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()) ==
            Task.FromResult(true));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Onboarding:PublicBaseUrl", "https://portal.mylegnd.com")
            })
            .Build();
        var provisioning = BuildProvisioning(db);
        var http = new DefaultHttpContext();
        var tempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());

        var piiProtector = new PiiProtector(new ServiceCollection()
            .AddDataProtection().Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>());
        var controller = new OnboardingController(
            db, config, provisioning,
            NullLogger<OnboardingController>.Instance,
            emailSender, piiProtector)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = tempData
        };
        // Simulate the request URL so BuildOnboardingLink can fall back safely.
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("portal.mylegnd.com");

        var model = new AgentPortal.Models.OnboardingInviteInputModel
        {
            FirstName = "Tom",
            LastName = "Recruit",
            Email = "  Tom.Recruit@EXAMPLE.COM  ",
            RoleType = "Agent"
        };

        await controller.CreateInvite(model);

        var invite = await db.OnboardingInvites.SingleAsync();
        Assert.Equal("tom.recruit@example.com", invite.NormalizedEmail);
    }

    // -----------------------------------------------------------------------
    // 8. OnboardingController_DuplicateEmailBlockedIfActiveInviteExists
    // -----------------------------------------------------------------------
    [Fact]
    public async Task OnboardingController_DuplicateEmailBlockedIfActiveInviteExists()
    {
        using var db = BuildDb();

        // Seed an active "Invited" invite for this email.
        db.OnboardingInvites.Add(new OnboardingInvite
        {
            Id = Guid.NewGuid(),
            TokenHash = "existing-hash",
            FirstName = "Tom",
            LastName = "Recruit",
            Email = "tom.recruit@example.com",
            NormalizedEmail = "tom.recruit@example.com",
            RoleType = "Agent",
            Status = "Invited",
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(7),
            CreatedBy = "owner@example.com"
        });
        await db.SaveChangesAsync();

        var emailSender = Mock.Of<IEmailSender>(s =>
            s.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()) ==
            Task.FromResult(true));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Onboarding:PublicBaseUrl", "https://portal.mylegnd.com")
            })
            .Build();
        var provisioning = BuildProvisioning(db);
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("portal.mylegnd.com");
        var tempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());

        var piiProtector = new PiiProtector(new ServiceCollection()
            .AddDataProtection().Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>());
        var controller = new OnboardingController(
            db, config, provisioning,
            NullLogger<OnboardingController>.Instance,
            emailSender, piiProtector)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = tempData
        };

        var model = new AgentPortal.Models.OnboardingInviteInputModel
        {
            FirstName = "Tom",
            LastName = "Recruit",
            Email = "Tom.Recruit@EXAMPLE.COM",
            RoleType = "Agent"
        };

        var result = await controller.CreateInvite(model);

        // Should return the Index view with a model error — not redirect.
        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(model.Email)));

        // Only the original invite must remain — no second row created.
        var count = await db.OnboardingInvites.CountAsync();
        Assert.Equal(1, count);
    }
}
