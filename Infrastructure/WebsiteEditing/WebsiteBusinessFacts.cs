using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
namespace Infrastructure.WebsiteEditing;

public sealed class WebsiteBusinessFacts
{
    public string ContactEmail { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Hours { get; set; } = "";
    public string Locations { get; set; } = "";
    public string Services { get; set; } = "";
    public static WebsiteBusinessFacts Sanitize(WebsiteBusinessFacts facts) => new()
    {
        ContactEmail = Trim(facts.ContactEmail, 254), Phone = Trim(facts.Phone, 80),
        Hours = Trim(facts.Hours, 4000), Locations = Trim(facts.Locations, 8000), Services = Trim(facts.Services, 12000)
    };
    private static string Trim(string? value, int max) => new((value ?? "").Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(max).ToArray());
    public static async Task<WebsiteBusinessFacts> LoadAsync(MasterAppDbContext db, Guid businessId, CancellationToken ct = default)
    {
        var json = await db.CommerceBusinessStorefrontSettings.AsNoTracking().Where(s => s.CommerceBusinessId == businessId).Select(s => s.PublicFactsJson).SingleOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(json) ? new() : Sanitize(JsonSerializer.Deserialize<WebsiteBusinessFacts>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new());
    }
}
