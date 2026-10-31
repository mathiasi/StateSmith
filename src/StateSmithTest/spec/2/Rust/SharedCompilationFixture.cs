#nullable enable

using StateSmith.Output.UserConfig;
using StateSmith.Runner;
using StateSmith.SmGraph;
using StateSmithTest.Processes;

namespace Spec.Spec2.Rust;

/// <summary>
/// Rust spike. Generates `test-misc/rust-spec2/src/spec2_sm.rs` and runs `cargo build --release` once.
/// </summary>
public class SharedCompilationFixture
{
    public static string OutputDirectory => Spec2Fixture.Spec2Directory + "../../../test-misc/rust-spec2/";
    public static string SrcDirectory => OutputDirectory + "src/";

    public SharedCompilationFixture()
    {
        var action = (SmRunner runner) =>
        {
            runner.Settings.transpilerId = TranspilerId.Rust;
            runner.AlgoOrTranspilerUpdated();

            // Runs before any other transformation so we only touch code from the diagram.
            runner.SmTransformer.transformationPipeline.Insert(0, new TransformationStep(action: (sm) =>
            {
                sm.VisitRecursively((node) =>
                {
                    foreach (var behavior in node.Behaviors)
                    {
                        behavior.actionCode = RustifyDiagramCode(behavior.actionCode);
                        behavior.guardCode = RustifyDiagramCode(behavior.guardCode);
                    }
                });
            }));
        };

        Spec2Fixture.CompileAndRun(new MyGlueFile(), SrcDirectory, action: action);

        SimpleProcess process = new()
        {
            WorkingDirectory = OutputDirectory,
            ProgramPath = "cargo",
            Args = "build --release --quiet"
        };
        process.Run(timeoutMs: SimpleProcess.DefaultLongTimeoutMs);
    }

    /// <summary>
    /// Spec2 diagram code is C like. The only construct that isn't valid Rust is `x++`.
    /// </summary>
    private static string RustifyDiagramCode(string str)
    {
        str = str.Replace("++", " += 1");
        return str;
    }

    public class MyGlueFile : IRenderConfigRust
    {
        string IRenderConfig.FileTop => @"
            // any text you put in IRenderConfig.FileTop (like this comment) will be written to the generated file
            ";

        string IRenderConfigRust.Imports => @"
            use crate::printer::{trace, trace_guard};
            ";

        string IRenderConfig.VariableDeclarations => @"
            count: i32 = 0,
            ";

        string IRenderConfig.AutoExpandedVars => @"
            auto_var_1: i32 = 0,
            ";

        public class Expansions : Spec2GenericVarExpansions
        {
            // trace() and trace_guard() default expansions already produce `trace(..)` and `trace_guard(..)` free function calls.
        }
    }
}
