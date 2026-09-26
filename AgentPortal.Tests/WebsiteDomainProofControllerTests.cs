using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteDomainProofControllerTests
{
    [Fact]
    public async Task PendingBindingReturnsOwnershipProofWithoutEditorTicketInfrastructure()
    {
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var business = new CommerceBusiness
        {
            Key = "proof-business",
            DisplayName = "Proof Business",
            LegalName = "Proof Business",
            Status = "Active",
            IsActive = true
        };
        var binding = new WebsiteDomainBinding
        {
            CommerceBusinessId = business.Id,
            Hostname = "business.example.com",
            Status = "pending",
            CertificateStatus = "active"
        };
        db.CommerceBusinesses.Add(business);
        db.Set<WebsiteDomainBinding>().Add(binding);
        await db.SaveChangesAsync();

        var controller = new WebsiteDomainProofController(
            db,
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Request.Host = new HostString(binding.Hostname);

        var result = await controller.DomainProof();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void PublicProofControllerHasOnlyMinimalDependencies()
    {
        var constructor = Assert.Single(typeof(WebsiteDomainProofController).GetConstructors());
        Assert.Equal(
            new[] { typeof(MasterAppDbContext), typeof(IConfiguration) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
