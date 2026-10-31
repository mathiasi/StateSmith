#nullable enable

using System.Text;
using StateSmith.Output.UserConfig;
using StateSmith.Output.Algos.Balanced1;

namespace StateSmith.Output.Gil.Rust;

public class GilToRust : IGilTranspiler
{
    private readonly StringBuilder fileSb = new();

    private readonly ICodeFileWriter codeFileWriter;
    private readonly RenderConfigRustVars renderConfigRust;
    private readonly RenderConfigBaseVars renderConfig;
    private readonly IOutputInfo outputInfo;
    private readonly RoslynCompiler roslynCompiler;
    private readonly CodeStyleSettings codeStyleSettings;
    private readonly NameMangler nameMangler;

    public GilToRust(IOutputInfo outputInfo, RenderConfigRustVars renderConfigRust, RenderConfigBaseVars renderConfig, ICodeFileWriter codeFileWriter, RoslynCompiler roslynCompiler, CodeStyleSettings codeStyleSettings, NameMangler nameMangler)
    {
        this.nameMangler = nameMangler;
        this.outputInfo = outputInfo;
        this.renderConfigRust = renderConfigRust;
        this.renderConfig = renderConfig;
        this.codeFileWriter = codeFileWriter;
        this.roslynCompiler = roslynCompiler;
        this.codeStyleSettings = codeStyleSettings;
    }

    public void TranspileAndOutputCode(string gilCode)
    {
        RustGilVisitor gilVisitor = new(gilCode, fileSb, renderConfigRust, renderConfig, roslynCompiler, codeStyleSettings);
        gilVisitor.Process();

        PostProcessor.PostProcess(fileSb);

        codeFileWriter.WriteFile($"{outputInfo.OutputDirectory}{nameMangler.BaseFileName}.rs", code: fileSb.ToString());
    }
}
