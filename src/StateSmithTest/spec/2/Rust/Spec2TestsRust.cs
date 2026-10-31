using Xunit;
using StateSmithTest.spec2;
using StateSmithTest.Processes;
using StateSmith.Output;
using System.Text.RegularExpressions;

namespace Spec.Spec2.Rust;

public class Spec2TestsRust : Spec2Tests, IClassFixture<SharedCompilationFixture>
{
    override public string PostInc => " += 1";

    public override string RunProcess(string testEvents)
    {
        SimpleProcess process = new()
        {
            WorkingDirectory = SharedCompilationFixture.OutputDirectory,
            ProgramPath = System.IO.Path.GetFullPath(SharedCompilationFixture.OutputDirectory + "target/release/rust-spec2"),
            Args = testEvents
        };
        process.Run(SimpleProcess.DefaultLongTimeoutMs);

        string output = process.StdOutput;
        output = StringUtils.RemoveEverythingBefore(output, "\nIGNORE_OUTPUT_BEFORE_THIS\n").Trim();
        output = Regex.Replace(output, @"[\s\S]*\nCLEAR_OUTPUT_BEFORE_THIS_AND_FOR_THIS_EVENT_DISPATCH\n[\s\S]*?\n\n", "").Trim();

        output = PreProcessOutput(output);

        return output;
    }
}
