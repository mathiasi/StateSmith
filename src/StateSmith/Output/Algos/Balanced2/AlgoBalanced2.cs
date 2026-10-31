#nullable enable

using StateSmith.Common;
using StateSmith.Output.Algos.Balanced1;
using StateSmith.Output.Gil;
using StateSmith.Output.UserConfig;
using StateSmith.Runner;
using StateSmith.SmGraph;
using StateSmith.SmGraph.Validation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StateSmith.Output.Algos.Balanced2;

public class AlgoBalanced2 : AlgoBalanced1
{
    private const string FirstAncestorHandlerComment = "First ancestor handler for this event";

    /// <summary>
    /// Just to be extra safe. Related to ensuring no infinite loop in "exit up to" function.
    /// https://github.com/StateSmith/StateSmith/issues/391
    /// </summary>
    public HashSet<NamedVertex> verticesThatNeedExitStateIdAdjustment = new();

    EventHandlerBuilder2 eventHandlerBuilder2;

    public AlgoBalanced2(NameMangler mangler, PseudoStateHandlerBuilder pseudoStateHandlerBuilder, EnumBuilder enumBuilder, RenderConfigBaseVars renderConfig, EventHandlerBuilder2 eventHandlerBuilder2, CodeStyleSettings styler, AlgoBalanced1Settings settings, IAlgoEventIdToString algoEventIdToString, IAlgoStateIdToString algoStateIdToString, StandardFileHeaderPrinter standardFileHeaderPrinter, RenderConfigPortableVars? portable = null, PortableTimers? portableTimers = null, StateSmith.Input.Expansions.IExpander? expander = null) :
        base(mangler, pseudoStateHandlerBuilder, enumBuilder, renderConfig, eventHandlerBuilder2, styler, settings, algoEventIdToString, algoStateIdToString, standardFileHeaderPrinter, portable, portableTimers, expander)
    {
        this.eventHandlerBuilder2 = eventHandlerBuilder2;
    }

    override protected void FinalCheck()
    {
        // https://github.com/StateSmith/StateSmith/issues/391
        var unhandledVertices = verticesThatNeedExitStateIdAdjustment.Except(eventHandlerBuilder2.verticesWithExitStateIdAdjustment);

        if (unhandledVertices.Any())
        {
            throw new VertexValidationException(unhandledVertices.First(), "Found vertex what wasn't handled properly for exiting.");
        }
    }

    protected override bool SupportsRegions => true;

    override protected string GetAlgoName()
    {
        return nameof(AlgorithmId.Balanced2);
    }

    override protected void OutputAlgoBalanced1StructPointers()
    {
        if (!RegionHelper.AnyRegions(Sm))
            return;

        // EXPERIMENTAL orthogonal regions
        file.AppendIndentedLine();
        file.AppendIndentedLine("// Active state of each orthogonal region (ROOT while the region is inactive). Feel free to inspect, but don't modify.");
        foreach (var region in OrderedRegions())
        {
            file.AppendIndentedLine($"public {mangler.SmStateEnumType} {mangler.SmRegionStateIdVarName(region)} = {mangler.SmQualifiedStateEnumValue(Sm)};");
        }

        file.AppendIndentedLine();
        file.AppendIndentedLine("// Used internally by the region dispatchers. Don't modify.");
        file.AppendIndentedLine($"private bool {mangler.SmRegionEventUnhandledVarName};");
        foreach (var parent in OrthogonalParents())
        {
            file.AppendIndentedLine($"private bool {mangler.SmRegionsExitedVarName(parent)};");
        }
    }

    private List<OrthoState> OrderedRegions() => Sm.GetOrderedNamedVerticesCopy().OfType<OrthoState>().ToList();
    private List<NamedVertex> OrthogonalParents() => Sm.GetOrderedNamedVerticesCopy().Where(v => v.HasRegions()).ToList();

    /// <summary>States whose scope is <paramref name="scope"/> (null: the root scope). Regions are in their own scope.</summary>
    private IEnumerable<NamedVertex> StatesInScope(OrthoState? scope) => Sm.GetOrderedNamedVerticesCopy().Where(v => v.Scope() == scope);

    override protected void OutputExitUpToFunction()
    {
        file.AppendIndentedLine("// This function is used when StateSmith doesn't know what the active leaf state is at");
        file.AppendIndentedLine("// compile time due to sub states or when multiple states need to be exited.");

        string desired_state = mangler.MangleVarName("desired_state");

        file.AppendIndented($"private void {mangler.SmExitUpToFuncName}({ConstMarker}{mangler.SmStateEnumType} {desired_state})");
        file.StartCodeBlock();

        file.AppendIndented($"while (this.{mangler.SmStateIdVarName} != {desired_state})");
        file.StartCodeBlock();
        {
            file.AppendIndented($"switch (this.{mangler.SmStateIdVarName})");
            file.StartCodeBlock();
            {
                string SEP = settings.allowSingleLineSwitchCase ? " " : "\n" + file.GetIndent();

                foreach (var namedVertex in StatesInScope(null))
                {
                    if (namedVertex is StateMachine)
                    {
                        // do nothing. let default case handle this.
                    }
                    else
                    {
                        file.AppendIndented();
                        file.AppendIndentNewlines($"case {mangler.SmQualifiedStateEnumValue(namedVertex)}:{SEP}");

                        string exitFunctionName = mangler.SmTriggerHandlerFuncName(namedVertex, TriggerHelper.TRIGGER_EXIT);
                        file.AppendIndentNewlines($"this.{exitFunctionName}();{SEP}");
                        file.AppendIndentNewlines($"break;\n");
                        file.AppendWithoutIndent("\n");

                        verticesThatNeedExitStateIdAdjustment.Add(namedVertex);
                    }
                }

                file.AppendIndented();
                file.AppendIndentNewlines($"default:{SEP}return;  // Just to be safe. Prevents infinite loop if state ID memory is somehow corrupted.");
                file.AppendWithoutIndent("\n");
            }
            file.FinishCodeBlock(forceNewLine: true);
        }
        file.FinishCodeBlock(forceNewLine: true);

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();

        if (RegionHelper.AnyRegions(Sm))
        {
            OutputRegionFunctions();
        }
    }

    //------------------------------------------------------------------------------------------
    // Orthogonal regions (EXPERIMENTAL). See RegionHelper for the model and terms.
    //------------------------------------------------------------------------------------------

    private void OutputRegionFunctions()
    {
        string eventIdParameterName = mangler.MangleVarName("event_id");
        string SEP = settings.allowSingleLineSwitchCase ? " " : "\n" + file.GetIndent();

        foreach (var region in OrderedRegions())
        {
            string regionVar = mangler.SmRegionStateIdVarName(region);

            // exit up to (within the region)
            file.AppendIndentedLine($"// Exits states in region `{region.Name}` until `desired_state` is active. ROOT exits the whole region.");
            string desired_state = mangler.MangleVarName("desired_state");
            file.AppendIndented($"private void {mangler.SmRegionExitUpToFuncName(region)}({mangler.SmStateEnumType} {desired_state})");
            file.StartCodeBlock();
            file.AppendIndented($"while (this.{regionVar} != {desired_state})");
            file.StartCodeBlock();
            file.AppendIndented($"switch (this.{regionVar})");
            file.StartCodeBlock();
            foreach (var v in StatesInScope(region))
            {
                file.AppendIndented();
                file.AppendIndentNewlines($"case {mangler.SmQualifiedStateEnumValue(v)}:{SEP}");
                file.AppendIndentNewlines($"this.{mangler.SmTriggerHandlerFuncName(v, TriggerHelper.TRIGGER_EXIT)}();{SEP}");
                file.AppendIndentNewlines("break;\n");
                verticesThatNeedExitStateIdAdjustment.Add(v);
            }
            file.AppendIndented();
            file.AppendIndentNewlines($"default:{SEP}return;  // Just to be safe. Prevents infinite loop if state ID memory is somehow corrupted.");
            file.AppendWithoutIndent("\n");
            file.FinishCodeBlock(forceNewLine: true);
            file.FinishCodeBlock(forceNewLine: true);
            file.FinishCodeBlock(forceNewLine: true);
            file.AppendIndentedLine();

            // default entry: enter the region, then follow its initial transition
            file.AppendIndentedLine($"// Enters region `{region.Name}` through its initial state.");
            file.AppendIndented($"private void {mangler.SmRegionEnterDefaultFuncName(region)}()");
            file.StartCodeBlock();
            file.AppendIndentedLine($"this.{mangler.SmTriggerHandlerFuncName(region, TriggerHelper.TRIGGER_ENTER)}();");
            var initialState = region.Children.OfType<InitialState>().Single();
            eventHandlerBuilder.OutputTransitionCode(new Behavior(region, initialState), noAncestorHandlesEvent: true, checkForExiting: false);
            file.FinishCodeBlock(forceNewLine: true);
            file.AppendIndentedLine();

            // dispatch: returns true if the region consumed the event
            file.AppendIndentedLine($"// Offers an event to region `{region.Name}`. Returns true if the region consumed it.");
            file.AppendIndented($"private bool {mangler.SmRegionDispatchFuncName(region)}({mangler.SmEventEnumType} {eventIdParameterName})");
            file.StartCodeBlock();
            file.AppendIndentedLine($"this.{mangler.SmRegionEventUnhandledVarName} = false;");
            file.AppendIndented($"switch (this.{regionVar})");
            file.StartCodeBlock();
            foreach (var v in StatesInScope(region).Where(v => v != region))
            {
                file.AppendIndentedLine($"// STATE: {v.Name}");
                file.AppendIndentedLine($"case {mangler.SmQualifiedStateEnumValue(v)}:");
                file.IncreaseIndentLevel();
                if (v.HasRegions())
                {
                    file.AppendIndentedLine($"this.{mangler.SmRegionsDispatchFuncName(v)}({eventIdParameterName});");
                }
                else
                {
                    OutputStateEventHandlerCode(v, eventIdParameterName, unhandledStatement: $"this.{mangler.SmRegionEventUnhandledVarName} = true;");
                }
                file.AppendIndentedLine("break;");
                file.DecreaseIndentLevel();
                file.AppendIndentedLine();
            }
            file.AppendIndentedLine($"default: this.{mangler.SmRegionEventUnhandledVarName} = true; break;");
            file.FinishCodeBlock(forceNewLine: true);
            file.AppendIndentedLine($"return !this.{mangler.SmRegionEventUnhandledVarName};");
            file.FinishCodeBlock(forceNewLine: true);
            file.AppendIndentedLine();
        }

        foreach (var parent in OrthogonalParents())
        {
            string exitedVar = mangler.SmRegionsExitedVarName(parent);
            bool parentInRegion = parent.ContainingRegion() != null;

            file.AppendIndentedLine($"// Dispatches an event to the regions of `{parent.Name}` (in region order). The event goes to `{parent.Name}`");
            file.AppendIndentedLine($"// and its ancestors only if no region consumed it. Stops early if a region's transition left `{parent.Name}`.");
            file.AppendIndented($"private void {mangler.SmRegionsDispatchFuncName(parent)}({mangler.SmEventEnumType} {eventIdParameterName})");
            file.StartCodeBlock();
            file.AppendIndentedLine("bool consumed = false;");
            file.AppendIndentedLine($"this.{exitedVar} = false;");
            foreach (var region in parent.Regions())
            {
                file.AppendIndentedLine();
                file.AppendIndented($"if (this.{mangler.SmRegionDispatchFuncName(region)}({eventIdParameterName}))");
                file.StartCodeBlock();
                file.AppendIndentedLine("consumed = true;");
                file.FinishCodeBlock(forceNewLine: true);
                file.AppendIndented($"if (this.{exitedVar})");
                file.StartCodeBlock();
                file.AppendIndentedLine("return;");
                file.FinishCodeBlock(forceNewLine: true);
            }
            file.AppendIndentedLine();
            file.AppendIndentedLine($"this.{mangler.SmRegionEventUnhandledVarName} = false;");
            file.AppendIndented("if (consumed)");
            file.StartCodeBlock();
            file.AppendIndentedLine("return;");
            file.FinishCodeBlock(forceNewLine: true);
            file.AppendIndentedLine();
            file.AppendIndentedLine($"// No region consumed the event: `{parent.Name}` and its ancestors get it.");
            OutputStateEventHandlerCode(parent, eventIdParameterName, unhandledStatement: parentInRegion ? $"this.{mangler.SmRegionEventUnhandledVarName} = true;" : null);
            file.FinishCodeBlock(forceNewLine: true);
            file.AppendIndentedLine();
        }
    }

    override protected void OutputFuncDispatchEvent()
    {
        OutputFuncDispatchEventStart(out string eventIdParameterName);

        file.StartCodeBlock();
        {
            // optimization for single event
            // https://github.com/StateSmith/StateSmith/issues/388
            if (Sm.GetEventSet().Count == 1)
            {
                file.AppendIndentedLine("// This state machine design only has a single event type so we can safely assume");
                file.AppendIndentedLine($"// that the dispatched event is `{Sm.GetEventSet().Single()}` without checking the `{eventIdParameterName}` parameter.");
                file.AppendIndentedLine(GilCreationHelper.MarkVarAsUnused(eventIdParameterName) + " // This line prevents an 'unused variable' compiler warning"); // Note! transpilers that don't need this will skip this line and trailing comments/trivia.
                file.AppendIndentedLine();
            }

            file.AppendIndented($"switch (this.{mangler.SmStateIdVarName})");
            file.StartCodeBlock();
            {
                bool needsLineSpacer = false;
                foreach (var namedVertex in StatesInScope(null))
                {
                    if (needsLineSpacer)
                    {
                        file.AppendIndentedLine();
                    }
                    needsLineSpacer = true;

                    file.AppendIndentedLine($"// STATE: {namedVertex.Name}");
                    file.AppendIndentedLine($"case {mangler.SmQualifiedStateEnumValue(namedVertex)}:");

                    file.IncreaseIndentLevel();
                    if (namedVertex.HasRegions())
                    {
                        file.AppendIndentedLine($"this.{mangler.SmRegionsDispatchFuncName(namedVertex)}({eventIdParameterName});");
                    }
                    else
                    {
                        OutputStateEventHandlerCode(namedVertex, eventIdParameterName);
                    }
                    file.AppendIndentedLine("break;");
                    file.DecreaseIndentLevel();
                }
            }
            file.FinishCodeBlock(forceNewLine: true);
            file.AppendIndentedLine();
        }
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private bool IsStateEventHandlerEmpty(NamedVertex namedVertex, HashSet<string> stateEvents, IEnumerable<string> otherEvents)
    {
        if (stateEvents.Count > 0)
        {
            return false;
        }

        foreach (string evt in otherEvents)
        {
            NamedVertex? ancestor = namedVertex.FirstAncestorThatHandlesEvent(evt);
            if (ancestor != null)
            {
                return false;
            }
        }

        return true;
    }

    /// <param name="unhandledStatement">Regions only: code that reports "not consumed here" for events without a handler in this scope.</param>
    private void OutputStateEventHandlerCode(NamedVertex namedVertex, string eventIdParameterName, string? unhandledStatement = null)
    {
        var allEvents = Sm.GetEventSet();

        // get list of all events that are handled by this state
        HashSet<string> stateEvents = TriggerHelper.GetEvents(namedVertex);

        // optimization for when a switch is not required (single event)
        if (allEvents.Count == 1)
        {
            OutputStateSingleEventHandlerCode(namedVertex, allEvents, stateEvents, unhandledStatement);
        }
        else
        {
            // events that are not handled by this state
            var otherEvents = allEvents.Except(stateEvents);

            // determine if this switch statement is needed
            if (IsStateEventHandlerEmpty(namedVertex, stateEvents, otherEvents))
            {
                file.AppendIndentedLine($"// No events handled by this state (or its ancestors).");
                if (unhandledStatement != null)
                    file.AppendIndentedLine(unhandledStatement);
            }
            else
            {
                file.AppendIndented($"switch ({eventIdParameterName})");
                file.StartCodeBlock();
                {
                    foreach (string evt in stateEvents)
                    {
                        MaybeOutputEventHandler(namedVertex, evt);
                    }

                    if (otherEvents.Any())
                    {
                        foreach (string evt in otherEvents)
                        {
                            NamedVertex? ancestor = namedVertex.FirstAncestorThatHandlesEvent(evt);
                            var comment = (ancestor == null) ? string.Empty : FirstAncestorHandlerComment;

                            if (string.IsNullOrWhiteSpace(comment))
                            {
                                // do nothing
                            }
                            else
                            {
                                MaybeOutputEventHandler(ancestor, evt, comment: comment);
                            }
                        }
                    }

                    if (unhandledStatement != null)
                    {
                        file.AppendIndentedLine($"default: {unhandledStatement} break;");
                    }
                    else if (settings.outputSwitchDefault)
                    {
                        file.AppendIndentedLine();
                        file.AppendIndentedLine($"default: break; // to avoid \"unused enumeration value in switch\" warning");
                    }
                }
                file.FinishCodeBlock(forceNewLine: true);
            }
        }
    }

    /// <summary>
    /// optimization for when a switch is not required (single event in entire state machine).
    /// https://github.com/StateSmith/StateSmith/issues/388
    /// </summary>
    /// <exception cref="Exception"></exception>
    private void OutputStateSingleEventHandlerCode(NamedVertex namedVertex, IReadOnlySet<string> allEvents, HashSet<string> stateEvents, string? unhandledStatement = null)
    {
        string onlyEvent = allEvents.Single();
        if (stateEvents.Count > 1)
        {
            throw new Exception($"Unexpected number of events: {stateEvents.Count}. Only expected: `{onlyEvent}`.");
        }

        file.AppendIndented();
        bool outputHandler = false;
        if (stateEvents.Any())
        {
            outputHandler = true;
            OutputEventHandlerCall(namedVertex, onlyEvent);
        }
        else
        {
            var ancestor = namedVertex.FirstAncestorThatHandlesEvent(onlyEvent);
            if (ancestor != null)
            {
                outputHandler = true;
                OutputEventHandlerCall(ancestor, onlyEvent);
                AddFirstAncestorHandlerComment();
            }
        }

        if (!outputHandler)
        {
            if (unhandledStatement != null)
                file.AppendWithoutIndent(unhandledStatement + " ");
            file.AppendWithoutIndent($"// state and ancestors have no handler for `{onlyEvent}` event.");
        }

        file.AppendWithoutIndent("\n");
    }

    private void MaybeOutputEventHandler(NamedVertex? namedVertex, string evt, string comment = "")
    {
        string SEP = settings.allowSingleLineSwitchCase ? " " : "\n" + file.GetIndent();

        file.AppendIndented();
        file.AppendIndentNewlines($"case {mangler.SmEventEnumType}.{mangler.SmEventEnumValue(evt)}:{SEP}");

        if (namedVertex != null)
        {
            OutputEventHandlerCall(namedVertex, evt, sep: SEP);
        }

        if (!settings.allowSingleLineSwitchCase)
        {
            AddCommentLine(comment);
            file.AppendIndented(""); // triggers indent
        }

        file.AppendIndentNewlines($"break;");

        if (settings.allowSingleLineSwitchCase)
        {
            AddCommentLine(comment);
        }
        else
        {
            file.AppendWithoutIndent("\n");
        }
    }

    private void AddFirstAncestorHandlerComment()
    {
        AddCommentLine(FirstAncestorHandlerComment);
    }

    private void AddCommentLine(string comment)
    {
        if (comment.Length > 0)
        {
            file.AppendWithoutIndent(" // " + comment);
        }
        file.FinishLine();
    }

    private void OutputEventHandlerCall(NamedVertex namedVertex, string evt, string sep = " ")
    {
        string eventHandlerFuncName = mangler.SmTriggerHandlerFuncName(namedVertex, evt);
        file.AppendIndentNewlines($"this.{eventHandlerFuncName}();{sep}");
    }

    protected override void OutputEventHandlerDelegate()
    {
        // do nothing
    }
}
