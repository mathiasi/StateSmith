#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StateSmith.Output.UserConfig;

/// <summary>
/// EXPERIMENTAL "portable code" mode (north-star branch).
///
/// When <see cref="Variables"/> or <see cref="Types"/> is set, StateSmith treats ALL diagram action and guard code as portable
/// code (GIL, a C# subset) instead of raw target language code. Every transpiler then translates it, so one diagram
/// generates idiomatic code for every target language with no per-language glue.
///
/// TOML (can live in the diagram):
/// <code>
/// [RenderConfig.Portable]
/// Types = """
///   enum Reply { NONE, OK, BUSY }
///   """
/// Variables = """
///   uint now_ms = 0;          // set by the host before each dispatch
///   Reply reply = Reply.NONE;
///   bool pump_on = false;
///   """
/// </code>
/// Every declared variable is auto-expanded, so diagram code just writes `reply = Reply.OK;`.
/// Built-in time helpers (need <see cref="TimeVariable"/>): `after(ms)` (time since the owning state was entered) and `elapsed(t)`.
/// </summary>
public class RenderConfigPortableVars
{
    /// <summary>Type declarations in GIL (C#) syntax. Currently enums.</summary>
    public string Types = "";

    /// <summary>State machine variables in GIL (C#) syntax, one per line: `type name = initial_value;`.</summary>
    public string Variables = "";

    /// <summary>Name of the declared variable that holds the current time in ms (unsigned, wrapping). Used by `after()` and `elapsed()`.</summary>
    public string TimeVariable = "now_ms";

    public bool IsEnabled => Variables.Trim().Length > 0 || Types.Trim().Length > 0;

    public const string GilVarsPath = "this.vars.";

    private static readonly Regex varRegex = new(@"^\s*(?:public\s+)?[\w.<>\[\]?]+\s+(?<name>[_a-zA-Z]\w*)\s*(?:=|;)");

    public static bool IsCommentOrBlank(string line)
    {
        var t = line.Trim();
        return t.Length == 0 || t.StartsWith("//");
    }

    public IEnumerable<string> VariableLines() => StringUtils.SplitIntoLinesOrEmpty(Variables.Trim());

    public List<string> VariableNames()
    {
        return VariableLines().Where(l => !IsCommentOrBlank(l)).Select(l =>
        {
            var m = varRegex.Match(l);
            if (!m.Success)
                throw new System.ArgumentException($"Can't parse portable variable declaration `{l}`. Expected `type name = value;`.");
            return m.Groups["name"].Value;
        }).ToList();
    }
}
