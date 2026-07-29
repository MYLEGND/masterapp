namespace Infrastructure.DailyScripture;

public interface IDailyScriptureService
{
    DailyScripture GetForDate(DateOnly date);
    DailyScripture GetTodayUtc();
}

public sealed class DailyScriptureService : IDailyScriptureService
{
    private static readonly DailyScripture[] Catalog =
    [
        new("Psalm 23", "KJV", [
            "The LORD is my shepherd; I shall not want.",
            "He maketh me to lie down in green pastures: he leadeth me beside the still waters.",
            "He restoreth my soul: he leadeth me in the paths of righteousness for his name's sake.",
            "Yea, though I walk through the valley of the shadow of death, I will fear no evil: for thou art with me; thy rod and thy staff they comfort me.",
            "Thou preparest a table before me in the presence of mine enemies: thou anointest my head with oil; my cup runneth over.",
            "Surely goodness and mercy shall follow me all the days of my life: and I will dwell in the house of the LORD for ever."
        ]),
        new("Psalm 121", "KJV", [
            "I will lift up mine eyes unto the hills, from whence cometh my help.",
            "My help cometh from the LORD, which made heaven and earth.",
            "He will not suffer thy foot to be moved: he that keepeth thee will not slumber.",
            "Behold, he that keepeth Israel shall neither slumber nor sleep.",
            "The LORD is thy keeper: the LORD is thy shade upon thy right hand.",
            "The sun shall not smite thee by day, nor the moon by night.",
            "The LORD shall preserve thee from all evil: he shall preserve thy soul.",
            "The LORD shall preserve thy going out and thy coming in from this time forth, and even for evermore."
        ]),
        new("Psalm 1", "KJV", [
            "Blessed is the man that walketh not in the counsel of the ungodly, nor standeth in the way of sinners, nor sitteth in the seat of the scornful.",
            "But his delight is in the law of the LORD; and in his law doth he meditate day and night.",
            "And he shall be like a tree planted by the rivers of water, that bringeth forth his fruit in his season; his leaf also shall not wither; and whatsoever he doeth shall prosper.",
            "The ungodly are not so: but are like the chaff which the wind driveth away.",
            "Therefore the ungodly shall not stand in the judgment, nor sinners in the congregation of the righteous.",
            "For the LORD knoweth the way of the righteous: but the way of the ungodly shall perish."
        ]),
        new("Psalm 100", "KJV", [
            "Make a joyful noise unto the LORD, all ye lands.",
            "Serve the LORD with gladness: come before his presence with singing.",
            "Know ye that the LORD he is God: it is he that hath made us, and not we ourselves; we are his people, and the sheep of his pasture.",
            "Enter into his gates with thanksgiving, and into his courts with praise: be thankful unto him, and bless his name.",
            "For the LORD is good; his mercy is everlasting; and his truth endureth to all generations."
        ])
    ];

    public DailyScripture GetTodayUtc() => GetForDate(DateOnly.FromDateTime(DateTime.UtcNow));

    public DailyScripture GetForDate(DateOnly date)
    {
        var key = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        uint hash = 0;
        foreach (var character in key)
            hash = unchecked(hash * 31 + character);

        var selected = Catalog[hash % (uint)Catalog.Length];
        return selected with { Date = key };
    }
}

public sealed record DailyScripture(
    string Reference,
    string Translation,
    IReadOnlyList<string> Verses,
    string Date = "")
{
    public string Text => string.Join(" ", Verses);
}
