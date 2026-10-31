#nullable enable

using FluentAssertions;
using StateSmith.Runner;
using StateSmith.SmGraph;
using Xunit;

namespace StateSmithTest.Output.Portable;

/// <summary>
/// EXPERIMENTAL portable code mode (north-star branch): one diagram, diagram code written once, translated per language.
/// </summary>
public class PortableCodeTests
{
    private const string Diagram = """"
        @startuml TimerSm

        /'! $CONFIG : toml
        [RenderConfig.Portable]
        Types = """
        enum Light { OFF, ON }
        """
        Variables = """
        uint now_ms = 0;        // set by the host
        uint on_ms = 5000;
        Light light = Light.OFF;
        uint presses = 0;
        """
        '/

        [*] --> IDLE
        IDLE --> LIT : PRESS / presses = presses + 1;
        LIT : enter / light = Light.ON;
        LIT : exit / light = Light.OFF;
        LIT --> IDLE : after(on_ms) [presses < 100]
        LIT --> IDLE : 2. do [elapsed(0) > on_ms * 2]
        @enduml
        """";

    private static string Generate(TranspilerId transpilerId, string fileName)
    {
        var fakeFs = new CapturingCodeFileWriter();
        TestHelper.CaptureCodeGenRunSmRunnerForPlantUmlString(plantUmlText: Diagram, codeFileWriter: fakeFs, transpilerId: transpilerId);
        return fakeFs.GetSoleCaptureWithName(fileName).code;
    }

    [Fact]
    public void Rust()
    {
        var code = Generate(TranspilerId.Rust, "timer_sm.rs");

        // user types and variables declared once, emitted natively
        code.Should().Contain("pub enum Light { #[default] OFF, ON }");
        code.Should().Contain("pub light: Light,");
        code.Should().Contain("light: Light::OFF,");
        code.Should().Contain("pub const fn new() -> Self");

        // diagram code translated, not echoed
        code.Should().Contain("self.vars.light = Light::ON;");
        code.Should().Contain("self.vars.presses = self.vars.presses.wrapping_add(1);");

        // after(): entry time recorded, wrapping math
        code.Should().Contain("self.vars.LIT_entered_at = self.vars.now_ms;");
        code.Should().Contain("self.vars.now_ms.wrapping_sub(self.vars.LIT_entered_at)) >= self.vars.on_ms");
        code.Should().Contain("self.vars.on_ms.wrapping_mul(2)");

        // `after(on_ms) [presses < 100]` trigger is sugar for `do [after(on_ms) && (presses < 100)]`
        code.Should().Contain("// uml: do [after(on_ms) && (presses < 100)] TransitionTo(IDLE)");

        // next_deadline_ms(): a pure query over the active state's timers
        code.Should().Contain("pub fn next_deadline_ms(&self) -> u32");
        code.Should().Contain("if self.state_id == StateId::LIT");
        code.Should().Contain("duration = self.vars.on_ms;");
        code.Should().Contain("duration = (self.vars.on_ms.wrapping_mul(2)).wrapping_add(1);"); // `elapsed(0) > on_ms * 2` is strict, so + 1
    }

    [Fact]
    public void TypeScript()
    {
        var code = Generate(TranspilerId.TypeScript, "TimerSm.ts");

        code.Should().Contain("export enum Light { OFF, ON }");
        code.Should().Contain("public light: Light = Light.OFF;");
        code.Should().Contain("public now_ms: number = 0;");
        code.Should().Contain("this.vars.light = Light.ON;");
        code.Should().Contain("this.vars.presses = ((this.vars.presses + 1) >>> 0);");
        code.Should().Contain("this.vars.LIT_entered_at = this.vars.now_ms;");
        code.Should().Contain("((this.vars.now_ms - this.vars.LIT_entered_at) >>> 0)");
        code.Should().Contain("(Math.imul(this.vars.on_ms, 2) >>> 0)");
        code.Should().Contain("public nextDeadlineMs(): number");
    }

    [Fact]
    public void UnknownTriggerCallIsAnError()
    {
        var diagram = Diagram.Replace("after(on_ms) [presses < 100]", "every(on_ms)");
        var action = () => TestHelper.CaptureCodeGenRunSmRunnerForPlantUmlString(plantUmlText: diagram, codeFileWriter: new CapturingCodeFileWriter(), transpilerId: TranspilerId.Rust);
        action.Should().Throw<System.Exception>().Which.ToString().Should().Contain("Unsupported trigger `every(on_ms)`");
    }

    [Fact]
    public void ReplaceCalls_BalancedParens()
    {
        PortableTimeProcessor.ReplaceCalls("a && after(f(x) + (y)) || my_after(z)", "after", arg => $"<{arg}>")
            .Should().Be("a && <f(x) + (y)> || my_after(z)");
    }

    /// <summary>
    /// Portable code is compiled by Roslyn as part of GIL, so a typo fails at generation time for every target,
    /// instead of later in each language's compiler.
    /// </summary>
    [Fact]
    public void TypoFailsAtGenerationTime()
    {
        var diagram = Diagram.Replace("light = Light.ON;", "light = Light.ONN;");
        var action = () => TestHelper.CaptureCodeGenRunSmRunnerForPlantUmlString(plantUmlText: diagram, codeFileWriter: new CapturingCodeFileWriter(), transpilerId: TranspilerId.TypeScript);
        action.Should().Throw<System.Exception>();
    }

    [Fact]
    public void TimeHelpersNeedTimeVariable()
    {
        var diagram = Diagram.Replace("uint now_ms = 0;        // set by the host", "");
        var action = () => TestHelper.CaptureCodeGenRunSmRunnerForPlantUmlString(plantUmlText: diagram, codeFileWriter: new CapturingCodeFileWriter(), transpilerId: TranspilerId.Rust);
        action.Should().Throw<System.Exception>().WithMessage("*need a time variable*");
    }
}
