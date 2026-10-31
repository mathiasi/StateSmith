#nullable enable

using FluentAssertions;
using StateSmith.Runner;
using StateSmithTest.Processes;
using System;
using Xunit;

namespace StateSmithTest.Output.Rust;

/// <summary>
/// Generates Rust AND TypeScript from the SAME diagram: `test-misc/manetta-example/ManettaSm.plantuml`.
/// Run with: dotnet test --filter "FullyQualifiedName~ManettaExampleGenTests"
///
/// North star version: the diagram is self contained. Its `$CONFIG` block declares the types and variables
/// (`[RenderConfig.Portable]`), and all diagram code is portable code. There is no per-language glue here:
/// no render config classes, no expansions, no variable tables. Only the target language differs.
/// </summary>
public class ManettaExampleGenTests
{
    public static string ExampleDirectory => AppDomain.CurrentDomain.BaseDirectory + "../../../../test-misc/manetta-example/";

    [Fact]
    public void Generate()
    {
        Run(TranspilerId.Rust, ExampleDirectory + "rust/src/", s =>
        {
            s.style = new RustLightSmGenTests.RustfmtLikeStyle();
            s.simulation.enableGeneration = true; // the simulator is language independent
            s.simulation.outputDirectory = ExampleDirectory;
        });

        Run(TranspilerId.TypeScript, ExampleDirectory + "ts/");
    }

    private static void Run(TranspilerId transpilerId, string outDir, Action<RunnerSettings>? customize = null)
    {
        RunnerSettings settings = new(diagramFile: ExampleDirectory + "ManettaSm.plantuml", outputDirectory: outDir, transpilerId: transpilerId);
        settings.outputStateSmithVersionInfo = false;
        settings.propagateExceptions = true;
        settings.dumpGilCodeOnError = true;
        customize?.Invoke(settings);
        new SmRunner(settings, renderConfig: null).Run(); // no render config: everything is in the diagram
    }

    /// <summary>
    /// Regenerates, then plays `scenarios.txt` with the Rust runner (cargo) and the TypeScript runner (tsc + node).
    /// The two traces must be identical. Needs cargo, tsc and node on the PATH.
    /// </summary>
    [Fact]
    public void RustAndTypeScriptTracesMatch()
    {
        Generate();

        SimpleProcess rust = new() { WorkingDirectory = ExampleDirectory + "rust", ProgramPath = "cargo", Args = "run --quiet", throwOnStdErr = false }; // runners print `do` dispatch stats on stderr
        rust.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);

        SimpleProcess tsc = new() { WorkingDirectory = ExampleDirectory + "ts", ProgramPath = "tsc", Args = "-p ." };
        tsc.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);
        SimpleProcess node = new() { WorkingDirectory = ExampleDirectory + "ts", ProgramPath = "node", Args = "dist/runner.js", throwOnStdErr = false };
        node.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);

        rust.StdOutput.Should().NotBeNullOrWhiteSpace();
        node.StdOutput.Should().Be(rust.StdOutput);

        // A low-power host that sleeps until next_deadline_ms() instead of polling `do` must behave identically.
        SimpleProcess rustSleep = new() { WorkingDirectory = ExampleDirectory + "rust", ProgramPath = "cargo", Args = "run --quiet -- --sleep", throwOnStdErr = false };
        rustSleep.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);
        SimpleProcess nodeSleep = new() { WorkingDirectory = ExampleDirectory + "ts", ProgramPath = "node", Args = "dist/runner.js --sleep", throwOnStdErr = false };
        nodeSleep.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);

        rustSleep.StdOutput.Should().Be(rust.StdOutput);
        nodeSleep.StdOutput.Should().Be(rust.StdOutput);
        rustSleep.StdError.Should().Contain("do dispatches: 25 ");
        rust.StdError.Should().Contain("do dispatches: 2190 ");
    }
}
