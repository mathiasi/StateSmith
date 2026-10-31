#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StateSmith.Output.Algos.Balanced1;

namespace StateSmith.SmGraph;

/// <summary>
/// Orthogonal regions (EXPERIMENTAL, north-star branch).
///
/// Model: a state P whose children are <see cref="OrthoState"/> vertices (the regions). Each region contains its own states
/// and an initial state. While P is active, every region has exactly one active state.
///
/// Terms used in the code generator:
///  - scope: the region a state belongs to (its nearest enclosing region), or the root scope if none.
///    A region is in its own scope. Each scope has its own "state id" variable: the root scope uses `state_id`,
///    each region `{region}_state_id` (ROOT while the region is inactive).
///  - orthogonal parent: a state with regions (P above). In its enclosing scope it is a normal state.
/// </summary>
public static class RegionHelper
{
    /// <summary>Nearest enclosing region (strict ancestor), or null when in the root scope.</summary>
    public static OrthoState? ContainingRegion(this Vertex v)
    {
        var p = v.Parent;
        while (p != null)
        {
            if (p is OrthoState o)
                return o;
            p = p.Parent;
        }
        return null;
    }

    /// <summary>The scope a vertex belongs to: itself for a region, else its containing region. Null means the root scope.</summary>
    public static OrthoState? Scope(this Vertex v) => v as OrthoState ?? v.ContainingRegion();

    public static bool HasRegions(this Vertex v) => v.Children.OfType<OrthoState>().Any();

    /// <summary>Regions of an orthogonal parent, in region order.</summary>
    public static List<OrthoState> Regions(this Vertex v) => v.Children.OfType<OrthoState>().OrderBy(o => o.order).ThenBy(o => o.Name).ToList();

    public static bool AnyRegions(StateMachine sm) => sm.DescendantsOfType<OrthoState>().Any();

    public const string InStateFuncName = "in_state";

    private static readonly Regex inStateRegex = new($@"(?<![\w.$]){InStateFuncName}\s*\(\s*(?<name>[_a-zA-Z]\w*)\s*\)");

    /// <summary>
    /// `in_state(X)`: true while state X is active (X itself or any state inside it). Works across regions, which is how
    /// regions coordinate (e.g. a brew region waiting for `in_state(STEAM_BOILER_READY)`). Rewritten to a GIL range check
    /// on the state id variable of X's scope, so it works in every target language.
    /// </summary>
    public static string RewriteInState(string code, Vertex owner, NameMangler mangler)
    {
        if (!code.Contains(InStateFuncName))
            return code;

        var sm = owner;
        while (sm.Parent != null)
            sm = sm.Parent;
        var root = (StateMachine)sm;

        return inStateRegex.Replace(code, m =>
        {
            string name = m.Groups["name"].Value;
            var target = root.DescendantsOfType<NamedVertex>().FirstOrDefault(v => v.Name == name)
                ?? throw new System.ArgumentException($"`{InStateFuncName}({name})`: no state named `{name}`.");

            var scope = target.Scope();
            string stateVar = "this." + (scope == null ? mangler.SmStateIdVarName : mangler.SmRegionStateIdVarName(scope));
            var subtree = new SubtreeIdMapper().MapSubtree(root)[target];

            string first = $"{mangler.SmStateEnumType}.{mangler.SmStateEnumValue(target)}";
            string gil = subtree.subtreeEndVertex == target
                ? $"({stateVar} == {first})"
                : $"({stateVar} >= {first} && {stateVar} <= {mangler.SmStateEnumType}.{mangler.SmStateEnumValue(subtree.subtreeEndVertex)})";
            return $"$gil({gil})";
        });
    }
}
