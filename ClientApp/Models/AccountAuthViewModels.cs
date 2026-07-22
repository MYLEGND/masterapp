using System.ComponentModel.DataAnnotations;

namespace ClientApp.Models;

public sealed class ClientLoginViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string ReturnUrl { get; set; } = "/";
    public string? Message { get; set; }
}

public sealed class ActivationRequiredViewModel
{
    public string ReturnUrl { get; set; } = "/";
    public string Title { get; set; } = "Activation Required";
    public string Message { get; set; } = "Use your activation link to finish setting up access, or contact your agent if you need a new invitation.";
}
