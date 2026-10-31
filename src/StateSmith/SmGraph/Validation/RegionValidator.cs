#nullable enable

using System.Linq;

namespace StateSmith.SmGraph.Validation;

/// <summary>
/// Rules for orthogonal regions (EXPERIMENTAL, north-star branch). Anything outside these rules is rejected with a clear
/// error instead of generating code with surprising semantics.
/// </summary>
public static class RegionValidator
{
    public static void ValidateRegion(OrthoState region)
    {
        if (region.Parent is not State parent || parent is StateMachine || parent is OrthoState)
        {
            throw new VertexValidationException(region, "A region must be directly inside a state (its orthogonal parent). " +
                "Regions directly in the state machine root aren't supported: wrap them in a state.");
        }

        if (region.Behaviors.Any())
        {
            throw new VertexValidationException(region, "Regions can't have behaviors. Put them on the orthogonal parent state or on a state inside the region.");
        }

        int initialCount = region.Children.OfType<InitialState>().Count();
        if (initialCount != 1)
        {
            throw new VertexValidationException(region, $"A region must have exactly 1 initial state. Actual count: {initialCount}.");
        }

        foreach (var sibling in parent.Children)
        {
            if (sibling is OrthoState || sibling is NotesVertex)
                continue;

            throw new VertexValidationException(sibling, $"State `{parent.Name}` has regions, so everything inside it must be in a region. " +
                $"Found `{Vertex.Describe(sibling)}` directly inside it.");
        }
    }

    /// <summary>
    /// Transition rules. Called for every vertex after transformations.
    /// </summary>
    public static void ValidateTransitions(Vertex vertex)
    {
        foreach (var b in vertex.Behaviors.Where(b => b.TransitionTarget != null))
        {
            var target = b.TransitionTarget!;

            if (target is OrthoState)
            {
                throw new BehaviorValidationException(b, "A transition can't target a region. Target the orthogonal parent (enters every region) or a state inside the region.");
            }

            var lca = vertex.FindTransitionPathTo(target).leastCommonAncestor;
            if (lca != null && lca.HasRegions())
            {
                throw new BehaviorValidationException(b, "Transitions between regions of the same orthogonal state, or from an orthogonal state into one of its regions, aren't supported. " +
                    "Use an event, a variable or an `in_state()` guard to coordinate regions.");
            }

            // deep history into a region would only restore that one region: not supported
            if (vertex is HistoryVertex && target.ContainingRegion() != vertex.ContainingRegion())
            {
                throw new BehaviorValidationException(b, "History that reaches into an orthogonal region isn't supported. Use history inside the region instead.");
            }
        }
    }
}
