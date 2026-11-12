namespace Deadband.Core.Tep;

/// <summary>Tag catalog for the Tennessee Eastman Process (Downs and Vogel, 1993): 41 measurements + 11 manipulated variables.</summary>
public static class TepCatalog
{
    public const int TagCount = 52;
    public const double SamplePeriodSec = 180; // Braatz data: one observation every 3 minutes
    public const int TestFaultOnsetIndex = 160; // test runs: fault introduced after 8 h (sample 161, 1-based)

    public sealed record TagInfo(string Id, string Description, string Units, string Unit, bool IsAnalyzer, bool IsManipulated);

    private static readonly (string desc, string units, string unit)[] Meas =
    {
        ("A feed (stream 1)", "kscmh", "Feed"), ("D feed (stream 2)", "kg/h", "Feed"), ("E feed (stream 3)", "kg/h", "Feed"),
        ("A and C feed (stream 4)", "kscmh", "Feed"), ("Recycle flow (stream 8)", "kscmh", "Compressor"),
        ("Reactor feed rate (stream 6)", "kscmh", "Reactor"), ("Reactor pressure", "kPa g", "Reactor"),
        ("Reactor level", "%", "Reactor"), ("Reactor temperature", "degC", "Reactor"), ("Purge rate (stream 9)", "kscmh", "Separator"),
        ("Product separator temperature", "degC", "Separator"), ("Product separator level", "%", "Separator"),
        ("Product separator pressure", "kPa g", "Separator"), ("Product separator underflow (stream 10)", "m3/h", "Separator"),
        ("Stripper level", "%", "Stripper"), ("Stripper pressure", "kPa g", "Stripper"), ("Stripper underflow (stream 11)", "m3/h", "Stripper"),
        ("Stripper temperature", "degC", "Stripper"), ("Stripper steam flow", "kg/h", "Stripper"), ("Compressor work", "kW", "Compressor"),
        ("Reactor cooling water outlet temperature", "degC", "Reactor"), ("Separator cooling water outlet temperature", "degC", "Separator"),
    };

    private static readonly string[] Mv =
    {
        "D feed flow valve", "E feed flow valve", "A feed flow valve", "A and C feed flow valve", "Compressor recycle valve",
        "Purge valve", "Separator pot liquid flow valve", "Stripper liquid product flow valve", "Stripper steam valve",
        "Reactor cooling water flow valve", "Condenser cooling water flow valve",
    };

    public static readonly IReadOnlyList<TagInfo> Tags = Build();

    public static readonly IReadOnlyDictionary<int, string> FaultDescriptions = new Dictionary<int, string>
    {
        [1] = "A/C feed ratio step (B composition constant)", [2] = "B composition step (A/C ratio constant)",
        [3] = "D feed temperature step", [4] = "Reactor cooling water inlet temperature step",
        [5] = "Condenser cooling water inlet temperature step", [6] = "A feed loss",
        [7] = "C header pressure loss", [8] = "A, B, C feed composition random variation",
        [9] = "D feed temperature random variation", [10] = "C feed temperature random variation",
        [11] = "Reactor cooling water inlet temperature random variation", [12] = "Condenser cooling water inlet temperature random variation",
        [13] = "Reaction kinetics slow drift", [14] = "Reactor cooling water valve sticking",
        [15] = "Condenser cooling water valve sticking", [16] = "Unknown", [17] = "Unknown", [18] = "Unknown",
        [19] = "Unknown", [20] = "Unknown", [21] = "Stream 4 valve fixed at steady-state position",
    };

    private static List<TagInfo> Build()
    {
        var list = new List<TagInfo>(TagCount);
        for (int i = 0; i < 22; i++)
            list.Add(new TagInfo($"XMEAS{i + 1:00}", Meas[i].desc, Meas[i].units, Meas[i].unit, false, false));
        string[] comps = { "A", "B", "C", "D", "E", "F", "G", "H" };
        for (int i = 0; i < 6; i++) list.Add(new TagInfo($"XMEAS{23 + i:00}", $"Reactor feed analysis, component {comps[i]}", "mol%", "Reactor", true, false));
        for (int i = 0; i < 8; i++) list.Add(new TagInfo($"XMEAS{29 + i:00}", $"Purge gas analysis, component {comps[i]}", "mol%", "Separator", true, false));
        for (int i = 0; i < 5; i++) list.Add(new TagInfo($"XMEAS{37 + i:00}", $"Product analysis, component {comps[3 + i]}", "mol%", "Stripper", true, false));
        for (int i = 0; i < 11; i++) list.Add(new TagInfo($"XMV{i + 1:00}", Mv[i], "%", "Controls", false, true));
        return list;
    }

    public static int IndexOf(string tagId)
    {
        for (int i = 0; i < Tags.Count; i++) if (Tags[i].Id == tagId) return i;
        return -1;
    }
}
