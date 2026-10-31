namespace StateSmith.Output.UserConfig;

public class RenderConfigRustVars
{
    public string Imports = "";
    public string VarsDerives = "";
    public string ImplCode = "";

    public void SetFrom(IRenderConfigRust config, bool autoDeIndentAndTrim)
    {
        string Process(string str)
        {
            if (str.Trim().Length == 0)
                return "";

            if (autoDeIndentAndTrim)
                return StringUtils.DeIndentTrim(str);

            return str;
        }

        Imports = Process(config.Imports);
        VarsDerives = Process(config.VarsDerives);
        ImplCode = Process(config.ImplCode);
    }
}
