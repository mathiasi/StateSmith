#nullable enable

using System.Collections.Generic;
using StateSmith.SmGraph;
using System.Linq;
using StateSmith.Common;
using System;
using StateSmith.Output.Gil;
using System.Text;
using StateSmith.Output.UserConfig;
using StateSmith.Runner;

namespace StateSmith.Output.Algos.Balanced1;

public class AlgoBalanced1 : IGilAlgo
{
    protected readonly AlgoBalanced1Settings settings;
    protected readonly EnumBuilder enumBuilder;
    protected readonly RenderConfigBaseVars renderConfig;
    protected readonly NameMangler mangler;
    protected readonly OutputFile file;
    protected readonly EventHandlerBuilder eventHandlerBuilder;
    protected readonly PseudoStateHandlerBuilder pseudoStateHandlerBuilder;
    protected readonly IAlgoEventIdToString algoEventIdToString;
    protected readonly IAlgoStateIdToString algoStateIdToString;
    protected readonly StandardFileHeaderPrinter standardFileHeaderPrinter;
    private readonly GilCreationHelper gilCreationHelper;
    protected readonly RenderConfigPortableVars? portable;
    protected readonly PortableTimers? portableTimers;
    protected readonly StateSmith.Input.Expansions.IExpander? expander;

    protected StateMachine? _sm;
    protected const string AlgoWikiLink = "https://github.com/StateSmith/StateSmith/wiki/Algorithms";

    protected StateMachine Sm => _sm.ThrowIfNull("Must be set before use");

    protected static string ConstMarker => ""; // todo_low - put in an attribute like [ro] that will end up as `const` for languages that support that

    public AlgoBalanced1(NameMangler mangler, PseudoStateHandlerBuilder pseudoStateHandlerBuilder, EnumBuilder enumBuilder, RenderConfigBaseVars renderConfig, EventHandlerBuilder eventHandlerBuilder, CodeStyleSettings styler, AlgoBalanced1Settings settings, IAlgoEventIdToString algoEventIdToString, IAlgoStateIdToString algoStateIdToString, StandardFileHeaderPrinter standardFileHeaderPrinter, RenderConfigPortableVars? portable = null, PortableTimers? portableTimers = null, StateSmith.Input.Expansions.IExpander? expander = null)
    {
        this.portable = portable;
        this.portableTimers = portableTimers;
        this.expander = expander;
        this.mangler = mangler;
        this.file = new OutputFile(styler, new StringBuilder());
        this.pseudoStateHandlerBuilder = pseudoStateHandlerBuilder;
        this.enumBuilder = enumBuilder;
        this.renderConfig = renderConfig;
        this.eventHandlerBuilder = eventHandlerBuilder;
        this.settings = settings;
        this.algoEventIdToString = algoEventIdToString;
        this.algoStateIdToString = algoStateIdToString;
        this.standardFileHeaderPrinter = standardFileHeaderPrinter;
        this.gilCreationHelper = new GilCreationHelper();
    }

    /// <summary>Orthogonal regions (EXPERIMENTAL) are only implemented by Balanced2.</summary>
    protected virtual bool SupportsRegions => false;

    public string GenerateGil(StateMachine sm)
    {
        if (!SupportsRegions && RegionHelper.AnyRegions(sm))
        {
            throw new System.ArgumentException($"Orthogonal regions are only supported by algorithm `{nameof(AlgorithmId.Balanced2)}`.");
        }

        this._sm = sm;
        mangler.SetStateMachine(sm);
        eventHandlerBuilder.SetFile(this.file);

        OutputFileTopAlgoComment();

        file.AppendIndentedLine($"// Generated state machine");
        file.AppendIndented($"public class {mangler.SmTypeName}");

        StartClassBlock();
        GenerateInner();

        GilCreationHelper.AppendGilHelpersFuncs(file);

        EndClassBlock();
        FinalCheck();
        return file.ToString();
    }

    virtual protected void FinalCheck()
    {

    }

    private void OutputFileTopAlgoComment()
    {
        GilCreationHelper.AddFileTopComment(file, standardFileHeaderPrinter.GetFileGilHeader() +
            $"// Algorithm: {GetAlgoName()}. See {AlgoWikiLink}\n");
    }

    virtual protected string GetAlgoName()
    {
        return nameof(AlgorithmId.Balanced1);
    }

    private void EndClassBlock()
    {
        file.FinishCodeBlock();
    }

    private void StartClassBlock()
    {
        file.StartCodeBlock();
    }

    // this is a bit of a hack that helps create the proper indentation for the GIL to C99 step
    private void RunWithPossibleIndentation(Action action)
    {
        if (settings.skipClassIndentation)
            file.DecreaseIndentLevel();

        action();

        if (settings.skipClassIndentation)
            file.IncreaseIndentLevel();
    }

    private void GenerateInner()
    {
        RunWithPossibleIndentation(() =>
        {
            enumBuilder.OutputEventIdCode(file);
            file.AppendIndentedLine();
            enumBuilder.OutputStateIdCode(file);
            file.AppendIndentedLine();

            foreach (var h in Sm.historyStates)
            {
                enumBuilder.OutputHistoryIdCode(file, h);
                file.AppendIndentedLine();
            }

            OutputPortableTypes();

            OutputEventHandlerDelegate();
        });

        pseudoStateHandlerBuilder.output = file;
        pseudoStateHandlerBuilder.mangler = mangler;
        pseudoStateHandlerBuilder.Gather(Sm);
        pseudoStateHandlerBuilder.MapParents();

        OutputStructDefinition();

        RunWithPossibleIndentation(() =>
        {
            OutputFuncCtor();

            OutputFuncStart();
            OutputFuncDispatchEvent();
            MaybeOutputPortableTypedDispatch();

            OutputExitUpToFunction();

            OutputTriggerHandlers();
            MaybeOutputAdditionalFunctions();
            MaybeOutputPortableNextDeadline();
        });
    }

    protected virtual void OutputEventHandlerDelegate()
    {
        file.AppendIndentedLine($"// event handler type");
        file.AppendIndentedLine($"private delegate void {mangler.SmHandlerFuncType}();");   // todo: use attribute or something to mark delegate as having implicit {mangler.SmTypeName} sm argument?
        file.AppendIndentedLine();
    }

    private void MaybeOutputAdditionalFunctions()
    {
        if (settings.outputStateIdToStringFunction)
        {
            algoStateIdToString.CreateStateIdToStringFunction(file, Sm);
        }

        if (settings.outputEventIdToStringFunction)
        {
            algoEventIdToString.CreateEventIdToStringFunction(file, Sm);
        }

        if (settings.outputGetParentIdFunction)
        {
            GetParentIdFunctionGen.Generate(file, Sm, mangler);
        }
    }

    /// <summary>
    /// EXPERIMENTAL portable code mode (P3). For each event declared with data (`START_BREW(uint max_ms)`), emits
    /// `dispatch_start_brew(uint max_ms)`: stores the arguments, then dispatches the event.
    /// </summary>
    private void MaybeOutputPortableTypedDispatch()
    {
        if (portable?.IsEnabled != true)
            return;

        var smEvents = Sm.GetEventListCopy();
        foreach (var payload in portable.EventPayloads())
        {
            var smEvent = smEvents.FirstOrDefault(e => string.Equals(e, payload.eventName, System.StringComparison.OrdinalIgnoreCase))
                ?? throw new System.ArgumentException($"Event `{payload.eventName}` is declared in [RenderConfig.Portable] Events but no behavior uses it.");

            string rawName = "dispatch_" + payload.eventName.ToLower();
            string funcName = mangler is AbstractMangler am ? am.MangleFuncName(rawName) : rawName;
            string parameters = string.Join(", ", payload.args.Select(a => $"{a.type} {a.name}"));

            file.AppendIndentedLine($"// Dispatches `{payload.eventName}` with its data. Not thread safe.");
            file.AppendIndented($"public void {funcName}({parameters})");
            file.StartCodeBlock();
            foreach (var arg in payload.args)
            {
                file.AppendIndentedLine($"this.vars.{arg.name} = {arg.name};");
            }
            file.AppendIndentedLine($"this.{mangler.SmDispatchEventFuncName}({mangler.SmEventEnumType}.{mangler.SmEventEnumValue(smEvent)});");
            file.FinishCodeBlock();
            file.AppendIndentedLine();
        }
    }

    /// <summary>
    /// EXPERIMENTAL portable code mode. Emits `next_deadline_ms()`: ms until the next `after()` / `elapsed()` timer of an
    /// active state expires, so a host can sleep instead of polling `do`.
    /// </summary>
    private void MaybeOutputPortableNextDeadline()
    {
        if (portable?.IsEnabled != true || portableTimers == null || portableTimers.timers.Count == 0 || expander == null)
            return;

        string Expand(string code) => StateSmith.Input.Antlr4.ExpandingVisitor.ParseAndExpandCode(expander, code).Trim();
        string now = Expand(portable.TimeVariable);
        string stateVar = $"this.{mangler.SmStateIdVarName}";
        string funcName = mangler is AbstractMangler am ? am.MangleFuncName("next_deadline_ms") : "next_deadline_ms";
        var subtrees = new SubtreeIdMapper().MapSubtree(Sm);

        file.AppendIndentedLine("// Portable code (experimental): milliseconds until the next `after()` / `elapsed()` timer of an active state expires.");
        file.AppendIndentedLine("// Returns 0xFFFFFFFF when no timer is pending. A host can sleep this long, then dispatch the `do` event.");
        file.AppendIndentedLine("// Other guard conditions are ignored, so the host may wake early but never late. Call it again after every dispatch.");
        file.AppendIndented($"public uint {funcName}()");
        file.StartCodeBlock();
        file.AppendIndentedLine("uint best = 0xFFFFFFFF;");
        file.AppendIndentedLine("uint elapsed = 0;");
        file.AppendIndentedLine("uint duration = 0;");

        foreach (var group in portableTimers.timers.GroupBy(t => t.state))
        {
            NamedVertex state = group.Key;
            var subtree = subtrees[state];
            string first = $"{mangler.SmStateEnumType}.{mangler.SmStateEnumValue(state)}";
            string last = $"{mangler.SmStateEnumType}.{mangler.SmStateEnumValue(subtree.subtreeEndVertex)}";
            string isActive = subtree.subtreeEndVertex == state ? $"{stateVar} == {first}" : $"{stateVar} >= {first} && {stateVar} <= {last}";

            file.AppendIndentedLine();
            file.AppendIndentedLine(subtree.subtreeEndVertex == state ? $"// {Vertex.Describe(state)}" : $"// {Vertex.Describe(state)} and its sub states");
            file.AppendIndented($"if ({isActive})");
            file.StartCodeBlock();
            foreach (var timer in group)
            {
                string start = Expand(timer.start);
                string duration = Expand(timer.duration);
                if (timer.strict)
                    duration = $"({duration}) + 1";

                file.AppendIndentedLine($"elapsed = {now} - {start};");
                file.AppendIndentedLine($"duration = {duration};");
                file.AppendIndented("if (elapsed < duration && duration - elapsed < best)");
                file.StartCodeBlock();
                file.AppendIndentedLine("best = duration - elapsed;");
                file.FinishCodeBlock();
            }
            file.FinishCodeBlock();
        }

        file.AppendIndentedLine();
        file.AppendIndentedLine("return best;");
        file.FinishCodeBlock();
        file.AppendIndentedLine();
    }

    internal void OutputStructDefinition()
    {
        file.AppendIndentedLine($"// Used internally by state machine. Feel free to inspect, but don't modify.");
        file.AppendIndentedLine($"public {mangler.SmStateEnumType} {mangler.SmStateIdVarName};");

        OutputAlgoBalanced1StructPointers();

        if (IsVarsStructNeeded())
        {
            RunWithPossibleIndentation(() =>
            {
                file.AppendIndentedLine();
                file.AppendIndentedLine("// State machine variables. Can be used for inputs, outputs, user variables...");
                var type = settings.varsStructAsClass ? "class" : "struct";

                file.AppendIndented($"public {type} Vars");
                file.StartCodeBlock();
                {
                    foreach (var line in StringUtils.SplitIntoLinesOrEmpty(Sm.variables.Trim()))
                    {
                        file.AppendIndentedLine("public " + line);
                    }

                    if (portable?.IsEnabled == true)
                    {
                        foreach (var line in portable.VariableLines())
                        {
                            file.AppendIndentedLine(RenderConfigPortableVars.IsCommentOrBlank(line) ? line.Trim() : "public " + line.Trim());
                        }

                        var payloads = portable.EventPayloads();
                        foreach (var arg in portable.EventArgVars())
                        {
                            var events = string.Join(", ", payloads.Where(p => p.args.Contains(arg)).Select(p => p.eventName));
                            file.AppendIndentedLine($"public {arg.type} {arg.name} = {RenderConfigPortableVars.DefaultValueFor(arg.type)}; // event data: {events}");
                        }
                    }

                    foreach (var line in StringUtils.SplitIntoLinesOrEmpty(renderConfig.VariableDeclarations.Trim()))
                    {
                        file.AppendIndentedLine(gilCreationHelper.WrapRawCodeAsField(line));
                    }
                }
                file.FinishCodeBlock();
            });

            file.AppendIndentedLine();
            file.AppendIndentedLine("// Variables. Can be used for inputs, outputs, user variables...");
            file.AppendIndentedLine("public Vars vars = new Vars();");
        }

        if (portable?.HasFunctions == true)
        {
            RunWithPossibleIndentation(() =>
            {
                file.AppendIndentedLine();
                file.AppendIndentedLine("// Host. Set by the constructor. Diagram code calls its functions.");
                file.AppendIndentedLine($"public {RenderConfigPortableVars.HostInterfaceName(mangler.SmTypeName)} {RenderConfigPortableVars.HostFieldName};");
            });
        }
    }

    virtual protected void OutputAlgoBalanced1StructPointers()
    {
        file.AppendIndentedLine();
        file.AppendIndentedLine($"// Used internally by state machine. Don't modify.");
        file.AppendIndentedLine($"private {mangler.SmHandlerFuncType}? {mangler.SmAncestorEventHandlerVarName};");

        file.AppendIndentedLine();
        file.AppendIndentedLine($"// Used internally by state machine. Don't modify.");
        file.AppendIndentedLine($"private readonly {mangler.SmHandlerFuncType}?[] {mangler.SmCurrentEventHandlersVarName} = new {mangler.SmHandlerFuncType}[{mangler.SmEventEnumCount}];");

        file.AppendIndentedLine();
        file.AppendIndentedLine($"// Used internally by state machine. Don't modify.");
        file.AppendIndentedLine($"private {mangler.SmHandlerFuncType}? {mangler.SmCurrentStateExitHandlerVarName};");
    }

    /// <summary>
    /// Portable code mode: user types (enums) declared in GIL syntax are nested in the state machine class like EventId/StateId,
    /// so every transpiler emits them the same way it emits its own enums.
    /// </summary>
    private void OutputPortableTypes()
    {
        if (portable == null)
            return;

        if (portable.HasFunctions)
        {
            file.AppendIndentedLine("// Host: what the state machine calls in the outside world (portable code).");
            file.AppendIndented($"public interface {RenderConfigPortableVars.HostInterfaceName(mangler.SmTypeName)}");
            file.StartCodeBlock();
            foreach (var line in portable.FunctionLines())
            {
                file.AppendIndentedLine(line.Trim());
            }
            file.FinishCodeBlock();
            file.AppendIndentedLine();
        }

        if (portable.Types.Trim().Length == 0)
            return;

        file.AppendIndentedLine("// User types (portable code)");
        foreach (var line in StringUtils.SplitIntoLinesOrEmpty(portable.Types.Trim()))
        {
            var trimmed = line.Trim();
            bool isTypeStart = System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^(enum|struct|class)\b");
            file.AppendIndentedLine(isTypeStart ? "public " + trimmed : trimmed);
        }
        file.AppendIndentedLine();
    }

    internal bool IsVarsStructNeeded()
    {
        if (portable?.IsEnabled == true)
        {
            return true;
        }

        if (Sm.variables.Length > 0)
        {
            return true;
        }

        return IsVariableDeclarationsNonEmpty();
    }

    private bool IsVariableDeclarationsNonEmpty()
    {
        return StringUtils.RemoveCCodeComments(renderConfig.VariableDeclarations).Trim().Length > 0;
    }

    /// <summary>
    /// Ctor is short for constructor
    /// </summary>
    internal void OutputFuncCtor()
    {
        file.AppendIndentedLine();
        file.AppendIndentedLine("// State machine constructor. Must be called before start or dispatch event functions. Not thread safe.");
        if (portable?.HasFunctions == true)
        {
            string host = RenderConfigPortableVars.HostFieldName;
            file.AppendIndented($"public {mangler.SmTypeName}({RenderConfigPortableVars.HostInterfaceName(mangler.SmTypeName)} {host})");
            file.StartCodeBlock();
            file.AppendIndentedLine($"this.{host} = {host};");
            file.FinishCodeBlock();
        }
        else
        {
            file.AppendIndented($"public {mangler.SmTypeName}()");
            file.StartCodeBlock();
            file.FinishCodeBlock();
        }
        file.AppendIndentedLine();
    }

    internal void OutputFuncStart()
    {
        file.AppendIndentedLine("// Starts the state machine. Must be called before dispatching events. Not thread safe.");
        file.AppendIndented($"public void {mangler.SmStartFuncName}()");
        file.StartCodeBlock();
        file.AppendIndentedLine($"this.{mangler.SmTriggerHandlerFuncName(Sm, TriggerHelper.TRIGGER_ENTER)}();");

        var initialState = Sm.Children.OfType<InitialState>().Single();

        var getToInitialStateBehavior = new Behavior(Sm, initialState);

        //var tempSmAccess = eventHandlerBuilder.smAccess;
        //eventHandlerBuilder.smAccess = "this";
        eventHandlerBuilder.OutputTransitionCode(getToInitialStateBehavior, noAncestorHandlesEvent: true, checkForExiting: false);
        //eventHandlerBuilder.smAccess = tempSmAccess;

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    virtual protected void OutputExitUpToFunction()
    {
        file.AppendIndentedLine("// This function is used when StateSmith doesn't know what the active leaf state is at");
        file.AppendIndentedLine("// compile time due to sub states or when multiple states need to be exited.");

        string desired_state_exit_handler = mangler.MangleVarName("desired_state_exit_handler");

        file.AppendIndented($"private void {mangler.SmExitUpToFuncName}({ConstMarker}{mangler.SmHandlerFuncType} {desired_state_exit_handler})");
        file.StartCodeBlock();

        file.AppendIndented($"while (this.{mangler.SmCurrentStateExitHandlerVarName} != {desired_state_exit_handler})");
        file.StartCodeBlock();
        {
            file.AppendIndentedLine($"this.{mangler.SmCurrentStateExitHandlerVarName}!();");
        }
        file.FinishCodeBlock(forceNewLine: true);

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    virtual protected void OutputFuncDispatchEvent()
    {
        OutputFuncDispatchEventStart(out string eventIdParameterName);

        file.StartCodeBlock();
        {
            string behavior_func = mangler.MangleVarName("behavior_func");
            file.AppendIndentedLine($"{mangler.SmHandlerFuncType}? {behavior_func} = this.{mangler.SmCurrentEventHandlersVarName}[(int){eventIdParameterName}];");
            file.AppendIndentedLine();
            file.AppendIndented($"while ({behavior_func} != null)");
            {
                file.StartCodeBlock();
                file.AppendIndentedLine($"this.{mangler.SmAncestorEventHandlerVarName} = null;");
                file.AppendIndentedLine($"{behavior_func}();");
                file.AppendIndentedLine($"{behavior_func} = this.{mangler.SmAncestorEventHandlerVarName};");
                file.FinishCodeBlock(forceNewLine: true);
            }
        }
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    protected string OutputFuncDispatchEventStart(out string eventIdParameterName)
    {
        eventIdParameterName = mangler.MangleVarName("event_id");

        file.AppendIndentedLine("// Dispatches an event to the state machine. Not thread safe.");
        file.AppendIndentedLine($"// Note! This function assumes that the `{eventIdParameterName}` parameter is valid.");

        file.AppendIndented($"public void {mangler.SmDispatchEventFuncName}({mangler.SmEventEnumType} {eventIdParameterName})");
        return eventIdParameterName;
    }

    internal void OutputTriggerHandlers()
    {
        List<NamedVertex> namedVertices = Sm.GetOrderedNamedVerticesCopy();

        foreach (var state in namedVertices)
        {
            eventHandlerBuilder.OutputNamedStateHandlers(state);
        }
    }
}
