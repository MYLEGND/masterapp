using System;
using System.ComponentModel.DataAnnotations;

namespace Domain.Entities;

public class QuickBooksConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(450)]
    public string OwnerUserId { get; set; } = "";

    [Required, MaxLength(128)]
    public string RealmId { get; set; } = "";

    [Required]
    public string AccessTokenCipher { get; set; } = "";

    [Required]
    public string RefreshTokenCipher { get; set; } = "";

    public DateTime AccessTokenExpiresUtc { get; set; }
    public DateTime? RefreshTokenExpiresUtc { get; set; }

    public DateTime ConnectedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSyncUtc { get; set; }

    [MaxLength(64)]
    public string? LastSyncStatus { get; set; }

    [MaxLength(1000)]
    public string? LastSyncError { get; set; }

    public bool IsActive { get; set; } = true;
}
