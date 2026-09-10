using System.ComponentModel.DataAnnotations;
namespace AgentPortal.Models;

public sealed class CrmContactUpdate
{
    [Required, StringLength(100)] public string FirstName { get; set; } = "";
    [StringLength(100)] public string LastName { get; set; } = "";
    private string? email;
    [EmailAddress, StringLength(254)] public string? Email
    {
        get => email;
        set => email = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
    [StringLength(50)] public string? Phone { get; set; }
    [StringLength(50)] public string? Phone2 { get; set; }
    [StringLength(250)] public string? AddressLine { get; set; }
    [StringLength(100)] public string? City { get; set; }
    [StringLength(100)] public string? State { get; set; }
    [StringLength(20)] public string? ZipCode { get; set; }
    [Required] public DateTime? UpdatedUtc { get; set; }
}
