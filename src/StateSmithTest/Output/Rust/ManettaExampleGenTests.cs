#nullable enable

using StateSmith.Input.Expansions;
using StateSmith.Output;
using StateSmith.Output.UserConfig;
using StateSmith.Runner;
using StateSmithTest.Processes;
using FluentAssertions;
using System;
using System.Linq;
using Xunit;

namespace StateSmithTest.Output.Rust;

/// <summary>
/// Generates Rust AND TypeScript from the SAME diagram: `test-misc/manetta-example/ManettaSm.plantuml`.
/// Run with: dotnet test --filter "FullyQualifiedName~ManettaExampleGenTests"
///
/// Only the glue differs between the two languages:
///  - variable declarations (one shared table below, rendered per language),
///  - a few helper expansions (`set_reply(OK)`, `power_is(READY)`, `elapsed(t)`, ...).
/// </summary>
public class ManettaExampleGenTests
{
    public static string ExampleDirectory => AppDomain.CurrentDomain.BaseDirectory + "../../../../test-misc/manetta-example/";

    /// <summary>
    /// All state machine variables. Each one becomes an auto-expanded var, so diagram code can just write `now_ms`.
    /// kind: "ms" (u32 / number), "u32", "bool", or an enum type name (initialised to NONE / OFF / IDLE...).
    /// </summary>
    private static readonly (string name, string kind, string init, string comment)[] Vars = new[]
    {
        // ---- inputs written by the host ----
        ("now_ms", "ms", "0", "host: monotonic time, set before every dispatch"),
        ("last_write_at", "ms", "0", "host: now_ms of the last BLE write (any write is a heartbeat)"),
        ("cmd_max_ms", "ms", "0", "host: argument of START_BREW / ENTER_STEAM"),

        // ---- device configuration ----
        ("device_max_brew_ms", "ms", "1800000", "device cap, at most 30 min"),
        ("device_max_steam_ms", "ms", "600000", "device cap for steam, counts from when steam began"),
        ("heartbeat_timeout_ms", "ms", "10000", "5 s to 60 s, only for app-started brews"),
        ("auto_off_min", "u32", "30", "auto-off delay in minutes"),

        // ---- outputs ----
        ("reply", "Reply", "NONE", "answer to the last command"),
        ("phase", "Phase", "IDLE", ""),
        ("power", "Power", "OFF", ""),
        ("steam_state", "SteamState", "OFF", ""),
        ("stop_reason", "StopReason", "NONE", "why the last brew stopped"),
        ("start_source", "StartSource", "NONE", "who started the last brew"),
        ("pump_on", "bool", "false", ""),
        ("power_relay_on", "bool", "false", ""),
        ("steam_on", "bool", "false", "steam heater"),

        // ---- internal timestamps and caps ----
        ("brew_started_at", "ms", "0", ""),
        ("brew_cmd_max_ms", "ms", "0", ""),
        ("steam_started_at", "ms", "0", ""),
        ("steam_cmd_at", "ms", "0", "time of the last ENTER_STEAM (a repeat replaces the command cap)"),
        ("steam_cmd_max_ms", "ms", "0", ""),
        ("auto_off_from", "ms", "0", "later of the last TURN_ON and the end of the last brew"),
    };

    private static bool IsEnum(string kind) => kind is not ("ms" or "u32" or "bool");

    private static string RustVars() => string.Join("\n", Vars.Select(v =>
    {
        string type = v.kind switch { "ms" => "u32", "u32" => "u32", "bool" => "bool", _ => v.kind };
        string init = IsEnum(v.kind) ? $"{v.kind}::{v.init}" : v.init;
        string comment = v.comment.Length > 0 ? " // " + v.comment : "";
        return $"{v.name}: {type} = {init},{comment}";
    }));

    private static string TypeScriptVars() => string.Join("\n", Vars.Select(v =>
    {
        string type = v.kind switch { "ms" => "number", "u32" => "number", "bool" => "boolean", _ => v.kind };
        string init = IsEnum(v.kind) ? $"{v.kind}.{v.init}" : v.init;
        string comment = v.comment.Length > 0 ? " // " + v.comment : "";
        return $"public {v.name}: {type} = {init};{comment}";
    }));

    [Fact]
    public void Generate()
    {
        GenerateRust();
        GenerateTypeScript();
    }

    /// <summary>
    /// Regenerates, then plays `scenarios.txt` with the Rust runner (cargo) and the TypeScript runner (tsc + node).
    /// The two traces must be identical. Needs cargo, tsc and node on the PATH.
    /// </summary>
    [Fact]
    public void RustAndTypeScriptTracesMatch()
    {
        Generate();

        SimpleProcess rust = new() { WorkingDirectory = ExampleDirectory + "rust", ProgramPath = "cargo", Args = "run --quiet" };
        rust.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);

        SimpleProcess tsc = new() { WorkingDirectory = ExampleDirectory + "ts", ProgramPath = "tsc", Args = "-p ." };
        tsc.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);
        SimpleProcess node = new() { WorkingDirectory = ExampleDirectory + "ts", ProgramPath = "node", Args = "dist/runner.js" };
        node.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);

        rust.StdOutput.Should().NotBeNullOrWhiteSpace();
        node.StdOutput.Should().Be(rust.StdOutput);
    }

    private static RunnerSettings NewSettings(string outDir, TranspilerId transpilerId)
    {
        RunnerSettings settings = new(diagramFile: ExampleDirectory + "ManettaSm.plantuml", outputDirectory: outDir, transpilerId: transpilerId);
        settings.outputStateSmithVersionInfo = false;
        settings.propagateExceptions = true;
        settings.dumpGilCodeOnError = true;
        return settings;
    }

    private static void GenerateRust()
    {
        var settings = NewSettings(ExampleDirectory + "rust/src/", TranspilerId.Rust);
        settings.style = new RustLightSmGenTests.RustfmtLikeStyle();
        settings.simulation.enableGeneration = true; // the simulator is language independent
        settings.simulation.outputDirectory = ExampleDirectory;
        new SmRunner(settings, new RustConfig()).Run();
    }

    private static void GenerateTypeScript()
    {
        var settings = NewSettings(ExampleDirectory + "ts/", TranspilerId.TypeScript);
        new SmRunner(settings, new TypeScriptConfig()).Run();
    }

    //////////////////////////////////////////////////////////////////////////////
    // Expansions. Same names for both languages, different output.
    //////////////////////////////////////////////////////////////////////////////

#pragma warning disable IDE1006 // Naming Styles
    public abstract class ManettaExpansions : UserExpansionScriptBase
    {
        /// <summary>`Reply::OK` (Rust) or `Reply.OK` (TS)</summary>
        protected abstract string Enum(string type, string member);

        /// <summary>u32 milliseconds since `t`, wrapping like the device counter.</summary>
        public abstract string elapsed(string t);

        public string minutes(string n) => $"({n} * 60000)";

        public string set_reply(string v) => $"{VarsPath}reply = {Enum("Reply", v)}";
        public string set_phase(string v) => $"{VarsPath}phase = {Enum("Phase", v)}";
        public string set_power(string v) => $"{VarsPath}power = {Enum("Power", v)}";
        public string set_steam_state(string v) => $"{VarsPath}steam_state = {Enum("SteamState", v)}";
        public string set_stop_reason(string v) => $"{VarsPath}stop_reason = {Enum("StopReason", v)}";
        public string set_start_source(string v) => $"{VarsPath}start_source = {Enum("StartSource", v)}";

        public string power_is(string v) => $"({VarsPath}power == {Enum("Power", v)})";
        public string started_by(string v) => $"({VarsPath}start_source == {Enum("StartSource", v)})";
    }

    public class RustExpansions : ManettaExpansions
    {
        protected override string Enum(string type, string member) => $"{type}::{member}";
        public override string elapsed(string t) => $"{VarsPath}now_ms.wrapping_sub({t})";
    }

    public class TypeScriptExpansions : ManettaExpansions
    {
        protected override string Enum(string type, string member) => $"{type}.{member}";
        public override string elapsed(string t) => $"(({VarsPath}now_ms - {t}) >>> 0)";
    }
#pragma warning restore IDE1006 // Naming Styles

    public class RustConfig : IRenderConfigRust
    {
        string IRenderConfig.FileTop => "// Generated by StateSmith (Rust spike) from ManettaSm.plantuml. Do not edit.";
        string IRenderConfigRust.Imports => "use crate::manetta_types::*;";
        string IRenderConfig.AutoExpandedVars => RustVars();
        public class Expansions : RustExpansions { }
    }

    public class TypeScriptConfig : IRenderConfigTypeScript
    {
        string IRenderConfig.FileTop => """
            // Generated by StateSmith from ManettaSm.plantuml. Do not edit.
            import { Reply, Phase, Power, SteamState, StopReason, StartSource } from "./manetta_types";
            """;
        string IRenderConfig.AutoExpandedVars => TypeScriptVars();
        public class Expansions : TypeScriptExpansions { }
    }
}
