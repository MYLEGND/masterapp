using System.ComponentModel.DataAnnotations;
using Domain.Messaging;

namespace AgentPortal.Models;

public sealed record TranslationLimitsPageVm(
    TranslationGlobalLimitSnapshot GlobalLimit,
    TranslationFounderAccountSearchSnapshot Search);

public sealed class TranslationGlobalLimitInput
{
    [Required]
    [Range(typeof(long), "0", "9223372036854775807")]
    public long? CharacterAllowance { get; set; }
    public Guid? Version { get; set; }
}
