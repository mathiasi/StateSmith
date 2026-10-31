#nullable enable

using StateSmith.Common;
using StateSmith.Input.Expansions;
using StateSmith.Output.Algos.Balanced1;
using StateSmith.Output.UserConfig;
using StateSmith.SmGraph;
using System.Collections.Generic;

namespace StateSmith.Output.Algos.Balanced2;

public class EventHandlerBuilder2 : EventHandlerBuilder
{
    /// <summary>
    /// Just to be extra safe. Related to ensuring no infinite loop in "exit up to" function.
    /// https://github.com/StateSmith/StateSmith/issues/391
    /// </summary>
    public HashSet<NamedVertex> verticesWithExitStateIdAdjustment = new();

    public EventHandlerBuilder2(IExpander expander, PseudoStateHandlerBuilder pseudoStateHandlerBuilder, NameMangler mangler, UserExpansionScriptBases userExpansionScriptBases, AlgoBalanced1Settings algoBalanced1Settings, RenderConfigPortableVars? portable = null) : base(expander, pseudoStateHandlerBuilder, mangler, userExpansionScriptBases, algoBalanced1Settings, portable)
    {
    }

    //------------------------------------------------------------------------------------------
    // Orthogonal regions (EXPERIMENTAL). See RegionHelper for the model and terms.
    //------------------------------------------------------------------------------------------

    /// <summary>The state id variable for the scope a vertex is in: `state_id` or `{region}_state_id`.</summary>
    public string StateIdVarFor(Vertex v)
    {
        var scope = v.Scope();
        return scope == null ? mangler.SmStateIdVarName : mangler.SmRegionStateIdVarName(scope);
    }

    override protected void OutputExitUpToCall(NamedVertex leastCommonAncestor)
    {
        // exit within the scope that contains the LCA (a region has its own "exit up to" loop)
        var scope = leastCommonAncestor.Scope();
        string func = scope == null ? mangler.SmExitUpToFuncName : mangler.SmRegionExitUpToFuncName(scope);
        File.AppendIndentedLine($"this.{func}({mangler.SmQualifiedStateEnumValue(leastCommonAncestor)});");
    }

    /// <summary>An orthogonal parent exits all of its regions before running its own exit behaviors.</summary>
    override protected void OutputFuncStateExit_PreBehaviors(NamedVertex state)
    {
        if (!state.HasRegions())
            return;

        File.AppendIndentedLine("// Exit all regions first (in reverse region order).");
        var regions = state.Regions();
        regions.Reverse();
        foreach (var region in regions)
        {
            File.AppendIndentedLine($"this.{mangler.SmRegionExitUpToFuncName(region)}({mangler.SmQualifiedStateEnumValue(GetRoot(state))});");
        }
        File.AppendIndentedLine($"this.{mangler.SmRegionsExitedVarName(state)} = true;");
        File.RequestNewLineBeforeMoreCode();
    }

    private static StateMachine GetRoot(Vertex v)
    {
        while (v.Parent != null)
            v = v.Parent;
        return (StateMachine)v;
    }

    /// <summary>
    /// Entering an orthogonal parent enters all of its regions. Regions not on the transition path take their default
    /// (initial) entry here, before the path continues into its own region (if any).
    /// </summary>
    override protected void OnStateEnteredDuringTransition(NamedVertex entered, Vertex? next)
    {
        if (!entered.HasRegions())
            return;

        foreach (var region in entered.Regions())
        {
            if (region == next)
                continue;
            File.AppendIndentedLine($"this.{mangler.SmRegionEnterDefaultFuncName(region)}();");
        }
    }

    // todolow - rename to something generic
    override protected void OutputStateExitFunctionPointerAdjustments(NamedVertex state, NamedVertex parentState)
    {
        // do nothing
    }

    override public void OutputFuncStateExit(NamedVertex state)
    {
        if (state is StateMachine)
        {
            // state machine roots don't need an exit function for Balanced2
        }
        else
        {
            base.OutputFuncStateExit(state);
        }
    }

    override protected void OutputExitFunctionEndCode(NamedVertex state)
    {
        // ex: sm->state_id = LightSm_StateId_ROOT;

        var parent = (NamedVertex?)state.Parent;
        if (parent != null)
        {
            // a region becomes inactive (ROOT) when exited; other states hand the scope back to their parent
            var newValue = state is OrthoState ? GetRoot(state) : parent;
            File.AppendIndentedLine($"this.{StateIdVarFor(state)} = {mangler.SmQualifiedStateEnumValue(newValue)};");
            verticesWithExitStateIdAdjustment.Add(state);
        }
    }

    override protected void OutputFuncStateEnter_PreBehaviors(NamedVertex state)
    {
        // NOTE! We need to track state as we enter it, so we can exit up to it later.
        // Some specified behaviors allow you to pass through a state (enter and exit immediately) towards another state.
        // This requires us to track the state we are entering.
        File.AppendIndentedLine($"this.{StateIdVarFor(state)} = {mangler.SmQualifiedStateEnumValue(state)};");
        File.RequestNewLineBeforeMoreCode();
    }

    override protected void PreActionCode(Behavior behavior, string triggerName, bool noAncestorHandlesEvent, bool hasConsumeEventVar)
    {
        if (!TriggerHelper.IsEvent(triggerName))
        {
            return;
        }

        // if transition behavior, no need to consume event. This method isn't called for transition behaviors though.

        // if no ancestor handles event, no need to consume event.
        // Inside a region the region dispatcher needs to know though, so behave as if an ancestor exists.
        if (noAncestorHandlesEvent && behavior.OwningVertex.ContainingRegion() == null)
        {
            //File.AppendLine($"// No ancestor handles event {triggerName}. No need for `{consumeEventVarName}`.");
            return;
        }

        if (TriggerHelper.IsDoEvent(triggerName))
        {
            File.AppendIndentedLine($"// `do` events are not normally consumed.");
            return;
        }
        else
        {
            File.AppendIndentedLine($"// Consume event `{triggerName}`.");
            File.AppendIndentedLine($"{consumeEventVarName} = true;");
        }
    }

    override protected void PostActionCode(Behavior behavior, string triggerName, bool noAncestorHandlesEvent, bool hasConsumeEventVar)
    {
        
    }

    override protected void OutputStateEventHandlerFunctionTop(NamedVertex namedVertex, string eventName)
    {
        NamedVertex? nextHandlingState = namedVertex.FirstAncestorThatHandlesEvent(eventName);
        bool noAncestorHandlesEvent = nextHandlingState == null;

        bool inRegion = namedVertex.ContainingRegion() != null && TriggerHelper.IsEvent(eventName);
        bool hasConsumeEventVar = ActionCodeUsesConsumeEventVar(namedVertex, eventName) || !noAncestorHandlesEvent || inRegion;

        if (hasConsumeEventVar)
        {
            if (noAncestorHandlesEvent)
            {
                File.AppendIndentedLine($"// note: no ancestor consumes this event, but we output `bool {consumeEventVarName}` anyway because a user's design might rely on it.");
            }

            File.AppendIndentedLine($"bool {consumeEventVarName} = false;");
            File.AppendIndentedLine();
        }
        else
        {
            //File.AppendLine($"// No need to output {consumeEventVarName}.");
        }
    }

    override protected void OutputStateEventHandlerFunctionBottom(NamedVertex namedVertex, string eventName)
    {
        NamedVertex? nextHandlingState = namedVertex.FirstAncestorThatHandlesEvent(eventName);

        if (nextHandlingState == null && namedVertex.ContainingRegion() != null)
        {
            File.AppendIndentedLine($"// Not consumed inside this region: tell the region dispatcher (the orthogonal parent may handle it).");
            File.AppendIndented($"if (!{consumeEventVarName})");
            File.StartCodeBlock();
            {
                File.AppendIndentedLine($"this.{mangler.SmRegionEventUnhandledVarName} = true;");
            }
            File.FinishCodeBlock();
        }
        else if (nextHandlingState == null)
        {
            File.AppendIndentedLine($"// No ancestor handles this event.");
        }
        else
        {
            File.AppendIndentedLine($"// Check if event has been consumed before calling ancestor handler.");
            File.AppendIndented($"if (!{consumeEventVarName})");
            File.StartCodeBlock();
            {
                File.AppendIndentedLine($"this.{mangler.SmTriggerHandlerFuncName(nextHandlingState, eventName)}();");
            }
            File.FinishCodeBlock();
        }
    }

    override protected void OutputCompleteTransition(bool noAncestorHandlesEvent, NamedVertex namedVertexTarget)
    {
        File.AppendIndentedLine($"return;");
    }

    override protected void OutputNextAncestorHandler(NamedVertex? nextAncestorHandlingState, string triggerName)
    {
        // do nothing
    }
}
