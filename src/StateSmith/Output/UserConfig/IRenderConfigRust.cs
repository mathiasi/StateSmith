namespace StateSmith.Output.UserConfig;

/// <summary>
/// Rust specific render config. Experimental (spike). See https://github.com/StateSmith/StateSmith/issues/475
/// </summary>
public interface IRenderConfigRust : IRenderConfig
{
    /// <summary>
    /// Emitted near the top of the generated module, after the file level attributes.
    /// Use for `use` statements, e.g. `use crate::light::Light;`.
    /// </summary>
    string Imports => "";

    /// <summary>
    /// Extra `#[derive(...)]` items for the generated `Vars` struct. Defaults to none so that
    /// user types placed in `Vars` (e.g. a hardware driver) don't need to implement anything.
    /// Example: "Clone, Debug".
    /// </summary>
    string VarsDerives => "";

    /// <summary>
    /// Code placed inside the generated `impl StateMachine { ... }` block.
    /// Note that Rust also allows a separate user `impl` block for the state machine type in any module of the same crate.
    /// </summary>
    string ImplCode => "";
}
