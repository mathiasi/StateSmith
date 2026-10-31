#nullable enable

using FluentAssertions;
using StateSmith.Runner;
using StateSmithTest.Processes;
using System;
using Xunit;

namespace StateSmithTest.Output.Regions;

/// <summary>
/// EXPERIMENTAL orthogonal regions. Generates `test-misc/regions-test/RegionsSm.plantuml` to Rust and TypeScript,
/// runs both with event sequences and checks the traced behavior.
/// </summary>
public class RegionsTests
{
    public static string Dir => AppDomain.CurrentDomain.BaseDirectory + "../../../../test-misc/regions-test/";

    [Fact]
    public void Generate()
    {
        Run(TranspilerId.Rust, Dir + "rust/src/");
        Run(TranspilerId.TypeScript, Dir + "ts/");
    }

    /// <summary>
    /// Plays every line of `sequences.txt` in the Rust and the TypeScript runner. Both must print exactly `expected.txt`.
    /// Needs cargo, tsc and node on the PATH.
    /// </summary>
    [Fact]
    public void RustAndTypeScriptMatchExpectedTraces()
    {
        Generate();

        new SimpleProcess { WorkingDirectory = Dir + "rust", ProgramPath = "cargo", Args = "build --quiet" }.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);
        new SimpleProcess { WorkingDirectory = Dir + "ts", ProgramPath = "tsc", Args = "-p ." }.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);

        var rust = new System.Text.StringBuilder();
        var ts = new System.Text.StringBuilder();
        foreach (var line in System.IO.File.ReadAllLines(Dir + "sequences.txt"))
        {
            if (line.StartsWith("#") || line.Trim().Length == 0)
                continue;

            var r = new SimpleProcess { WorkingDirectory = Dir + "rust", ProgramPath = "cargo", Args = "run --quiet -- " + line };
            r.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);
            rust.Append($"### {line}\n").Append(r.StdOutput.TrimEnd('\n') + "\n");

            var t = new SimpleProcess { WorkingDirectory = Dir + "ts", ProgramPath = "node", Args = "dist/runner.js " + line };
            t.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);
            ts.Append($"### {line}\n").Append(t.StdOutput.TrimEnd('\n') + "\n");
        }

        string expected = System.IO.File.ReadAllText(Dir + "expected.txt");
        rust.ToString().Should().Be(expected);
        ts.ToString().Should().Be(expected);
    }

    //------------------------------------------------------------------------------------------
    // Validation: unsupported region shapes are rejected with a clear message.
    //------------------------------------------------------------------------------------------

    private static string Diagram(string extra) => $$"""
        @startuml BadSm
        [*] --> PAR
        state PAR {
            [*] --> A1
            state A1
            --
            [*] --> B1
            state B1
        }
        {{extra}}
        @enduml
        """;

    private static void ExpectError(string plantUml, string messagePart, AlgorithmId algorithmId = AlgorithmId.Balanced2, TranspilerId transpilerId = TranspilerId.TypeScript)
    {
        var action = () => TestHelper.CaptureCodeGenRunSmRunnerForPlantUmlString(plantUmlText: plantUml, codeFileWriter: new CapturingCodeFileWriter(), transpilerId: transpilerId, algorithmId: algorithmId);
        action.Should().Throw<Exception>().Which.ToString().Should().Contain(messagePart);
    }

    [Fact]
    public void ValidDiagramGenerates()
    {
        var action = () => TestHelper.CaptureCodeGenRunSmRunnerForPlantUmlString(plantUmlText: Diagram("A1 --> A1 : EV1"), codeFileWriter: new CapturingCodeFileWriter(), transpilerId: TranspilerId.TypeScript);
        action.Should().NotThrow();
    }

    [Fact]
    public void TransitionBetweenRegionsIsRejected() => ExpectError(Diagram("A1 --> B1 : EV1"), "Transitions between regions");

    [Fact]
    public void TransitionFromParentIntoRegionIsRejected() => ExpectError(Diagram("PAR --> B1 : EV1"), "Transitions between regions");

    [Fact]
    public void TransitionTargetingRegionIsRejected() => ExpectError(Diagram("A1 --> PAR_REGION_2 : EV1"), "can't target a region");

    [Fact]
    public void RegionSeparatorAtRootIsRejected() => ExpectError("""
        @startuml BadSm
        [*] --> A
        state A
        --
        state B
        @enduml
        """, "must be inside a composite state");

    [Fact]
    public void Balanced1IsRejected() => ExpectError(Diagram(""), "only supported by algorithm `Balanced2`", AlgorithmId.Balanced1, TranspilerId.CSharp);

    private static void Run(TranspilerId transpilerId, string outDir)
    {
        RunnerSettings settings = new(diagramFile: Dir + "RegionsSm.plantuml", outputDirectory: outDir, transpilerId: transpilerId);
        settings.outputStateSmithVersionInfo = false;
        settings.propagateExceptions = true;
        settings.dumpGilCodeOnError = true;
        settings.outputGilCodeAlways = transpilerId == TranspilerId.Rust;
        new SmRunner(settings, renderConfig: null).Run();
    }
}
