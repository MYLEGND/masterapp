namespace ParfaitApp.Security;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ParfaitInternalPageAttribute : Attribute
{
    public ParfaitInternalPageAttribute(
        string title,
        string group,
        string description,
        int groupOrder,
        int order)
    {
        Title = title;
        Group = group;
        Description = description;
        GroupOrder = groupOrder;
        Order = order;
    }

    public string Title { get; }
    public string Group { get; }
    public string Description { get; }
    public int GroupOrder { get; }
    public int Order { get; }
    public bool ShowInNavigation { get; set; } = true;
    public bool FounderOnly { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ParfaitInternalPageAccessAttribute : Attribute
{
    public ParfaitInternalPageAccessAttribute(string pageKey)
    {
        PageKey = pageKey;
    }

    public string PageKey { get; }
}
