#nullable enable

using StateSmith.Output.Algos.Balanced1;
using StateSmith.SmGraph;
using System.Text.RegularExpressions;

namespace StateSmith.Output.Gil.Rust;

/// <summary>
/// Spike naming choices:
/// - state handler functions are snake_case (`on_group_exit`). Lower-casing is collision free because StateSmith state names are
///   already unique ignoring case.
/// - history fields and enum types keep StateSmith's names (`ON_GROUP_history: ON_GROUP_HistoryId`) like every other target,
///   so `$gil(...)` history code and the shared spec expectations stay identical across languages.
/// - enum variants stay SCREAMING_CASE like the C/C#/Python targets (matches the diagram 1:1, no collision risk from re-casing).
/// The generated file carries `#![allow(non_camel_case_types, non_snake_case)]` for these.
/// </summary>
public class RustNameMangler : NameMangler
{
    /// <summary>
    /// Rust module files are snake_case: `LightSm` -> `light_sm.rs`.
    /// </summary>
    public override string BaseFileName => ToSnakeCase(Sm.Name);

    public static string ToSnakeCase(string name)
    {
        var s = Regex.Replace(name, @"([a-z0-9])([A-Z])", "$1_$2");
        s = Regex.Replace(s, @"([A-Z]+)([A-Z][a-z])", "$1_$2");
        return s.ToLower();
    }

    public override string SmTriggerHandlerFuncName(NamedVertex state, string triggerName)
    {
        return $"{SmStateName(state).ToLower()}_{triggerName.ToLower()}";
    }
}
