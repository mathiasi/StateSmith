#nullable enable

using StateSmith.Input.Expansions;
using System;

namespace StateSmith.Output.UserConfig;

public class DefaultExpansionsProcessor
{
    private readonly RenderConfigBaseVars renderConfig;
    private readonly IExpander expander;
    private readonly IExpansionVarsPathProvider expansionVarsPathProvider;
    private readonly RenderConfigPortableVars? portable;

    public DefaultExpansionsProcessor(RenderConfigBaseVars renderConfig, IExpander expander, IExpansionVarsPathProvider expansionVarsPathProvider, RenderConfigPortableVars? portable = null)
    {
        this.portable = portable;
        this.renderConfig = renderConfig;
        this.expander = expander;
        this.expansionVarsPathProvider = expansionVarsPathProvider;
    }

    public void AddExpansions()
    {
        string? anyTemplate = GetNonEmptyOrDefault(renderConfig.DefaultAnyExpTemplate, null);
        MaybeAddExpander(renderConfig.DefaultVarExpTemplate, anyTemplate, expander.SetDefaultVarExpander);
        MaybeAddExpander(renderConfig.DefaultFuncExpTemplate, anyTemplate, expander.SetDefaultFuncExpander);
    }

    public void MaybeAddExpander(string? template, string? defaultTemplate, Action<DefaultExpander> addingFunction)
    {
        var expander = MaybeBuildExpander(template, defaultTemplate);
        if (expander != null)
        {
            addingFunction(expander);
        }
    }

    private DefaultExpander? MaybeBuildExpander(string? template, string? defaultTemplate)
    {
        template = GetNonEmptyOrDefault(template, defaultTemplate);

        if (template == null)
        {
            return null;
        }

        var pathProvider = portable?.IsEnabled == true ? new FixedExpansionVarsPathProvider(RenderConfigPortableVars.GilVarsPath) : expansionVarsPathProvider;
        return new DefaultExpander(pathProvider, template);
    }

    private static string? GetNonEmptyOrDefault(string? value, string? defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }
}

/// <summary>Used by portable code mode.</summary>
public class FixedExpansionVarsPathProvider : IExpansionVarsPathProvider
{
    public FixedExpansionVarsPathProvider(string path) { ExpansionVarsPath = path; }
    public string ExpansionVarsPath { get; }
}
