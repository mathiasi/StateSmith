#nullable enable

using StateSmith.Common;
using StateSmith.Output.UserConfig;
using StateSmith.SmGraph.Validation;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace StateSmith.SmGraph;

/// <summary>
/// EXPERIMENTAL (north-star branch). Built-in time helpers for portable code mode:
/// <list type="bullet">
///   <item>`after(ms)`: true once `ms` milliseconds have passed since the state that owns the behavior was entered.
///         Typically used as `do [after(cmd_max_ms)]`. StateSmith adds a `{STATE}_entered_at` variable and an
///         entry action that records the time.</item>
///   <item>`elapsed(t)`: milliseconds since the timestamp `t` (unsigned, wrapping).</item>
/// </list>
/// Both rewrite to unsigned GIL subtraction on <see cref="RenderConfigPortableVars.TimeVariable"/>, which each
/// transpiler maps to wrapping arithmetic (Rust `wrapping_sub`, TypeScript `>>> 0`).
/// </summary>
public class PortableTimeProcessor
{
    public const string AfterFuncName = "after";
    public const string ElapsedFuncName = "elapsed";
    public const string EnteredAtPostfix = "_entered_at";

    private readonly RenderConfigPortableVars portable;
    private readonly PortableTimers timers;

    public PortableTimeProcessor(RenderConfigPortableVars portable, PortableTimers timers)
    {
        this.portable = portable;
        this.timers = timers;
    }

    public void Process(StateMachine sm)
    {
        if (!portable.IsEnabled)
            return;

        string now = portable.TimeVariable;
        var statesNeedingEntryTime = new List<NamedVertex>();
        bool usedTime = false;

        // Only collect here. The text rewrite happens at expansion time (see Rewrite) so the generated `// uml:` comments
        // still show what the diagram says (`after(brew_cmd_max_ms)`).
        sm.VisitRecursively(vertex =>
        {
            foreach (var b in vertex.Behaviors)
            {
                string code = b.guardCode + " " + b.actionCode;
                if (!UsesTimeHelper(code))
                    continue;

                usedTime = true;

                if (vertex is NamedVertex namedState && vertex is not StateMachine)
                    CollectTimers(namedState, b.guardCode);

                if (ContainsCall(code, AfterFuncName))
                {
                    if (vertex is not NamedVertex state || vertex is StateMachine)
                        throw new VertexValidationException(vertex, $"`{AfterFuncName}()` can only be used in behaviors of a state (it measures time since that state was entered).");

                    if (!statesNeedingEntryTime.Contains(state))
                        statesNeedingEntryTime.Add(state);
                }
            }
        });

        if (usedTime && !portable.VariableNames().Contains(now))
        {
            throw new ArgumentException($"`{AfterFuncName}()`/`{ElapsedFuncName}()` need a time variable. Declare `uint {now} = 0;` in [RenderConfig.Portable] Variables (or set TimeVariable).");
        }

        foreach (var state in statesNeedingEntryTime)
        {
            string enteredAt = EnteredAtVar(state);
            sm.variables += $"uint {enteredAt} = 0;\n";

            // record entry time BEFORE any user entry actions
            string action = $"{RenderConfigPortableVars.GilVarsPath}{enteredAt} = {RenderConfigPortableVars.GilVarsPath}{now};";
            state.AddBehavior(new Behavior(trigger: TriggerHelper.TRIGGER_ENTER, actionCode: action), index: 0);
        }
    }

    public static string EnteredAtVar(NamedVertex state) => state.Name + EnteredAtPostfix;

    /// <summary>
    /// Finds the timers a guard waits on, for <see cref="PortableTimers"/> (`next_deadline_ms()`):
    /// `after(X)` and `elapsed(T) >= X` / `elapsed(T) > X`. Other forms are still valid code, just not known deadlines.
    /// </summary>
    private void CollectTimers(NamedVertex state, string guard)
    {
        foreach (var (arg, _, end) in FindCalls(guard, AfterFuncName))
        {
            timers.Add(new PortableTimers.Timer(state, RenderConfigPortableVars.GilVarsPath + EnteredAtVar(state), arg.Trim(), strict: false));
        }

        foreach (var (arg, _, end) in FindCalls(guard, ElapsedFuncName))
        {
            var m = Regex.Match(guard[end..], @"^\s*(>=|>(?![>=]))");
            if (!m.Success)
                continue;

            string duration = ReadOperand(guard, end + m.Length);
            if (duration.Length > 0)
                timers.Add(new PortableTimers.Timer(state, arg.Trim(), duration, strict: m.Groups[1].Value == ">"));
        }
    }

    /// <summary>Reads an expression up to a top-level `&&`, `||`, `?`, `:`, `,`, unbalanced `)` or the end.</summary>
    private static string ReadOperand(string code, int start)
    {
        int depth = 0;
        int i = start;
        for (; i < code.Length; i++)
        {
            char c = code[i];
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0) break; depth--; }
            else if (depth == 0 && (c == '?' || c == ':' || c == ',')) break;
            else if (depth == 0 && i + 1 < code.Length && ((c == '&' && code[i + 1] == '&') || (c == '|' && code[i + 1] == '|'))) break;
        }
        return code[start..i].Trim();
    }

    /// <summary>Returns (argument, call start index, index after the closing paren) for each `funcName(...)` call.</summary>
    private static IEnumerable<(string arg, int start, int end)> FindCalls(string code, string funcName)
    {
        var regex = CallRegex(funcName);
        int pos = 0;
        while (true)
        {
            var m = regex.Match(code, pos);
            if (!m.Success)
                yield break;

            int argStart = m.Index + m.Length;
            int depth = 1;
            int i = argStart;
            for (; i < code.Length && depth > 0; i++)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')') depth--;
            }
            if (depth != 0)
                yield break;

            yield return (code[argStart..(i - 1)], m.Index, i);
            pos = i;
        }
    }

    /// <summary>
    /// Rewrites `after(x)` and `elapsed(t)` in diagram code into portable (GIL) time math. Called at expansion time.
    /// </summary>
    public static string Rewrite(string code, Behavior behavior, RenderConfigPortableVars portable)
    {
        if (!UsesTimeHelper(code))
            return code;

        string now = portable.TimeVariable;
        code = ReplaceCalls(code, ElapsedFuncName, arg => $"({now} - {Wrap(arg)})");

        if (ContainsCall(code, AfterFuncName))
        {
            var state = (NamedVertex)behavior.OwningVertex; // validated by Process()
            string enteredAt = RenderConfigPortableVars.GilVarsPath + EnteredAtVar(state);
            code = ReplaceCalls(code, AfterFuncName, arg => $"(({now} - {enteredAt}) >= {Wrap(arg)})");
        }

        return code;
    }

    private static bool UsesTimeHelper(string code) => ContainsCall(code, AfterFuncName) || ContainsCall(code, ElapsedFuncName);

    private static string Wrap(string arg)
    {
        arg = arg.Trim();
        return Regex.IsMatch(arg, @"^[\w.]+$") ? arg : $"({arg})";
    }

    private static Regex CallRegex(string funcName) => new($@"(?<![\w.$]){Regex.Escape(funcName)}\s*\(");

    public static bool ContainsCall(string code, string funcName) => CallRegex(funcName).IsMatch(code);

    /// <summary>
    /// Replaces `funcName(arg)` calls (balanced parentheses) using <paramref name="replacer"/>.
    /// </summary>
    public static string ReplaceCalls(string code, string funcName, Func<string, string> replacer)
    {
        var regex = CallRegex(funcName);
        var sb = new StringBuilder();
        int pos = 0;

        while (true)
        {
            var m = regex.Match(code, pos);
            if (!m.Success)
                break;

            int argStart = m.Index + m.Length;
            int depth = 1;
            int i = argStart;
            for (; i < code.Length && depth > 0; i++)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')') depth--;
            }

            if (depth != 0)
                throw new ArgumentException($"Unbalanced parentheses in `{funcName}(...)` call: `{code}`");

            string arg = code[argStart..(i - 1)];
            sb.Append(code, pos, m.Index - pos);
            sb.Append(replacer(arg));
            pos = i;
        }

        sb.Append(code, pos, code.Length - pos);
        return sb.ToString();
    }
}
