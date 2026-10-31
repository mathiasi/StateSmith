#nullable enable

using StateSmith.Input.Expansions;

namespace StateSmith.Output.UserConfig.AutoVars;

public class AutoExpandedVarsProcessor
{
    private readonly RenderConfigBaseVars renderConfig;
    private readonly IExpander expander;
    private readonly IExpansionVarsPathProvider expansionVarsPathProvider;
    private readonly IAutoVarsParser autoVarsParser;
    private readonly RenderConfigPortableVars? portable;

    public AutoExpandedVarsProcessor(RenderConfigBaseVars renderConfig, IExpander expander, IExpansionVarsPathProvider expansionVarsPathProvider, IAutoVarsParser autoVarsParser, RenderConfigPortableVars? portable = null)
    {
        this.portable = portable;
        this.renderConfig = renderConfig;
        this.expander = expander;
        this.expansionVarsPathProvider = expansionVarsPathProvider;
        this.autoVarsParser = autoVarsParser;
    }

    public void AddExpansions()
    {
        string toParse = renderConfig.AutoExpandedVars;
        var varsPath = expansionVarsPathProvider.ExpansionVarsPath;

        // portable code mode: declared variables are GIL fields, and diagram code is GIL, so use the GIL vars path.
        if (portable?.IsEnabled == true)
        {
            varsPath = RenderConfigPortableVars.GilVarsPath;
            foreach (var name in portable.VariableNames())
            {
                expander.AddVariableExpansion(name, varsPath + name);
            }
        }

        var identifiers = autoVarsParser.ParseIdentifiers(toParse);

        foreach (var identifier in identifiers)
        {
            expander.AddVariableExpansion(identifier, varsPath + identifier);
        }

        StringUtils.AppendInPlaceWithNewlineIfNeeded(ref renderConfig.VariableDeclarations, renderConfig.AutoExpandedVars);
    }
}
