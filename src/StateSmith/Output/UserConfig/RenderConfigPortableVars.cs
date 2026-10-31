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

    /// <summary>
    /// Host functions the diagram can call, in GIL (C#) interface syntax, one per line: `void set_pump(bool on);`.
    /// They become a host interface (`{StateMachine}Host`): a Rust trait the state machine is generic over, a TypeScript
    /// interface passed to the constructor. Diagram code calls them by name: `set_pump(true);`.
    /// </summary>
    public string Functions = "";

    /// <summary>
    /// Events that carry data, one per line: `START_BREW(uint max_ms)`. StateSmith generates a typed dispatch function
    /// (`dispatch_start_brew(max_ms)`) that stores the arguments and dispatches the event. Diagram code reads the
    /// argument by name (`max_ms`). Arguments with the same name share one variable, so their types must match.
    /// </summary>
    public string Events = "";

    /// <summary>Name of the declared variable that holds the current time in ms (unsigned, wrapping). Used by `after()` and `elapsed()`.</summary>
    public string TimeVariable = "now_ms";

    public bool IsEnabled => Variables.Trim().Length > 0 || Types.Trim().Length > 0 || HasFunctions || Events.Trim().Length > 0;

    public bool HasFunctions => Functions.Trim().Length > 0;

    public const string HostFieldName = "host";

    public static string HostInterfaceName(string smName) => smName + "Host";

    public const string GilVarsPath = "this.vars.";

    private static readonly Regex varRegex = new(@"^\s*(?:public\s+)?[\w.<>\[\]?]+\s+(?<name>[_a-zA-Z]\w*)\s*(?:=|;)");

    public static bool IsCommentOrBlank(string line)
    {
        var t = line.Trim();
        return t.Length == 0 || t.StartsWith("//");
    }

    public IEnumerable<string> VariableLines() => StringUtils.SplitIntoLinesOrEmpty(Variables.Trim());

    private static readonly Regex funcRegex = new(@"^\s*[\w.<>\[\]?]+\s+(?<name>[_a-zA-Z]\w*)\s*\(");

    public IEnumerable<string> FunctionLines() => StringUtils.SplitIntoLinesOrEmpty(Functions.Trim());

    public List<string> FunctionNames()
    {
        return FunctionLines().Where(l => !IsCommentOrBlank(l)).Select(l =>
        {
            var m = funcRegex.Match(l);
            if (!m.Success)
                throw new System.ArgumentException($"Can't parse portable function declaration `{l}`. Expected `return_type name(params);`.");
            return m.Groups["name"].Value;
        }).ToList();
    }

    /// <summary>`set_pump(true)` to `this.host.set_pump(true)` for declared host functions.</summary>
    public string RewriteHostCalls(string code)
    {
        if (!HasFunctions)
            return code;

        foreach (var name in FunctionNames())
        {
            code = Regex.Replace(code, $@"(?<![\w.$]){Regex.Escape(name)}(?=\s*\()", $"this.{HostFieldName}.{name}");
        }
        return code;
    }

    public record EventArg(string type, string name);
    public record EventPayload(string eventName, List<EventArg> args);

    private static readonly Regex eventRegex = new(@"^\s*(?<name>[_a-zA-Z]\w*)\s*\((?<args>[^)]*)\)\s*;?\s*(//.*)?$");

    public List<EventPayload> EventPayloads()
    {
        var result = new List<EventPayload>();
        foreach (var line in StringUtils.SplitIntoLinesOrEmpty(Events.Trim()).Where(l => !IsCommentOrBlank(l)))
        {
            var m = eventRegex.Match(line);
            if (!m.Success)
                throw new System.ArgumentException($"Can't parse portable event declaration `{line}`. Expected `EVENT_NAME(type arg, ...)`.");

            var args = m.Groups["args"].Value.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                .Select(a =>
                {
                    var parts = a.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2)
                        throw new System.ArgumentException($"Can't parse event argument `{a}` in `{line}`. Expected `type name`.");
                    return new EventArg(parts[0], parts[1]);
                }).ToList();
            result.Add(new EventPayload(m.Groups["name"].Value, args));
        }
        return result;
    }

    /// <summary>One variable per distinct argument name. Throws if the same name is used with different types.</summary>
    public List<EventArg> EventArgVars()
    {
        var byName = new Dictionary<string, EventArg>();
        foreach (var arg in EventPayloads().SelectMany(e => e.args))
        {
            if (byName.TryGetValue(arg.name, out var existing) && existing.type != arg.type)
                throw new System.ArgumentException($"Event argument `{arg.name}` is declared as both `{existing.type}` and `{arg.type}`.");
            byName[arg.name] = arg;
        }
        return byName.Values.ToList();
    }

    public static string DefaultValueFor(string gilType) => gilType switch
    {
        "bool" => "false",
        "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" => "0",
        "float" or "double" => "0",
        _ => throw new System.ArgumentException($"Event argument type `{gilType}` isn't supported yet (numbers and bool only).")
    };

    public List<string> VariableNames()
    {
        var names = DeclaredVariableNames();
        foreach (var arg in EventArgVars())
        {
            if (names.Contains(arg.name))
                throw new System.ArgumentException($"Event argument `{arg.name}` clashes with a variable of the same name.");
            names.Add(arg.name);
        }
        return names;
    }

    private List<string> DeclaredVariableNames()
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
