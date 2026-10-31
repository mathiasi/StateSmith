#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StateSmith.Output.UserConfig.AutoVars;

/// <summary>
/// Rust spike. Variable declarations look like `name: Type = const_init,` (one per line).
/// </summary>
public class RustAutoVarsParser : IAutoVarsParser
{
    public List<string> ParseIdentifiers(string code)
    {
        code = StringUtils.RemoveCCodeComments(code);

        var regex = new Regex(@"(?m)^\s*(?:pub(?:\([^)]*\))?\s+)?(?<identifier>[_a-zA-Z]\w*)\s*:");
        return regex.Matches(code).Cast<Match>().Select(m => m.Groups["identifier"].Value).ToList();
    }
}
