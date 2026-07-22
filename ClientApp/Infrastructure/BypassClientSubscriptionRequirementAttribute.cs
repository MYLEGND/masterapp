namespace ClientApp.Infrastructure;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class BypassClientSubscriptionRequirementAttribute : Attribute
{
}
