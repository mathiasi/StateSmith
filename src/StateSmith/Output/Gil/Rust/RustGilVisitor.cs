#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StateSmith.Common;
using StateSmith.Output.UserConfig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace StateSmith.Output.Gil.Rust;

/// <summary>
/// SPIKE. Transpiles GIL (a C# subset) to a single Rust module file that only depends on `core`.
/// See https://github.com/StateSmith/StateSmith/issues/475 and RUST_SPIKE.md at the repo root.
///
/// Output shape:
///  - nested GIL enums, consts and the `Vars` struct are hoisted to module level (Rust has no nested types).
///  - the state machine class becomes `pub struct Sm { pub state_id, pub vars }` + `impl Sm { ... }`.
///  - the GIL constructor becomes `pub const fn new() -> Self` (non-const if a user var has no initializer).
///  - `switch` becomes `match` and the C# `break` that ends each switch section is dropped
///    (a `break` inside a `match` inside a `while` would exit the loop in Rust).
/// </summary>
public class RustGilVisitor : CSharpSyntaxWalker
{
    public const string FileAttributes = "#![allow(non_camel_case_types, non_snake_case, non_upper_case_globals, dead_code, unreachable_code, unused_mut, unused_assignments, unused_parens, clippy::needless_return, clippy::single_match, clippy::upper_case_acronyms)]";
    public const string EnumDerives = "#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]";

    private StringBuilder sb;
    private readonly RenderConfigRustVars renderConfigRust;
    private readonly GilTranspilerHelper transpilerHelper;
    private readonly RenderConfigBaseVars renderConfig;
    private readonly CodeStyleSettings codeStyleSettings;
    private readonly SemanticModel model;

    private SyntaxToken? tokenToSkip;
    private bool skipLeadingTrivia = false;
    private INamedTypeSymbol? smClassSymbol;

    /// <summary>
    /// Set to false if any field initializer can't be evaluated in a `const fn`.
    /// </summary>
    private bool ctorCanBeConst = true;

    private string Indent1 => codeStyleSettings.Indent1;

    public RustGilVisitor(string gilCode, StringBuilder sb, RenderConfigRustVars renderConfigRust, RenderConfigBaseVars renderConfig, RoslynCompiler roslynCompiler, CodeStyleSettings codeStyleSettings) : base(SyntaxWalkerDepth.StructuredTrivia)
    {
        this.sb = sb;
        this.renderConfig = renderConfig;
        this.renderConfigRust = renderConfigRust;
        this.codeStyleSettings = codeStyleSettings;
        transpilerHelper = GilTranspilerHelper.Create(this, gilCode, roslynCompiler);
        model = transpilerHelper.model;
    }

    public void Process()
    {
        transpilerHelper.PreProcess();

        // inner attributes must come before any item, but may follow comments.
        sb.AppendLine(FileAttributes);
        sb.AppendLine();
        sb.AppendLineIfNotBlank(renderConfig.FileTop);
        sb.AppendLineIfNotBlank(renderConfigRust.Imports, optionalTrailer: "\n");

        this.Visit(transpilerHelper.root);
    }

    //=====================================================================================
    // Types
    //=====================================================================================

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (transpilerHelper.HandleSpecialGilEmitClasses(node)) return;

        if (smClassSymbol != null)
        {
            // nested class (only `Vars` when `varsStructAsClass` is set)
            EmitVarsStruct(node);
            return;
        }

        smClassSymbol = model.GetDeclaredSymbol(node).ThrowIfNull();
        string smName = node.Identifier.Text;

        // leading trivia of class (like `// Generated state machine`)
        string classLeadingTrivia = Capture(() => VisitLeadingTrivia(node.GetFirstToken()));

        // Hoist nested types and consts out of the class. Rust has no nested types.
        {
            string hoisted = Capture(() =>
            {
                foreach (var member in node.Members)
                {
                    switch (member)
                    {
                        case EnumDeclarationSyntax:
                            Visit(member);
                            break;
                        case StructDeclarationSyntax:
                        case ClassDeclarationSyntax:
                            EmitVarsStruct((TypeDeclarationSyntax)member);
                            break;
                        case FieldDeclarationSyntax field when field.IsConst():
                            EmitConst(field);
                            break;
                    }
                }
            });
            sb.Append(StringUtils.DeIndent(hoisted));
        }

        // state machine struct
        var fieldInits = new List<(string name, string init)>();
        sb.Append(classLeadingTrivia.TrimStart('\r', '\n'));
        sb.Append($"pub struct {smName}");
        OpenBrace("");
        sb.Append(TrimLeadingBlankLines(Capture(() =>
        {
            foreach (var field in node.Members.OfType<FieldDeclarationSyntax>().Where(f => !f.IsConst()))
            {
                EmitStructField(field, fieldInits);
            }
        })));
        sb.AppendLine("}");
        sb.AppendLine();

        // impl block
        sb.Append($"impl {smName}");
        OpenBrace("");

        if (!string.IsNullOrWhiteSpace(renderConfigRust.ImplCode))
        {
            sb.AppendLine(StringUtils.Indent(renderConfigRust.ImplCode, Indent1));
            sb.AppendLine();
        }

        var ctor = node.Members.OfType<ConstructorDeclarationSyntax>().SingleOrDefault();
        sb.Append(TrimLeadingBlankLines(Capture(() => EmitConstructor(ctor, fieldInits))));

        foreach (var member in node.Members.Where(m => m is MethodDeclarationSyntax))
        {
            Visit(member);
        }

        sb.AppendLine("}");
        sb.AppendLine();

        EmitDefaultImpl(smName);
    }

    private static string TrimLeadingBlankLines(string code) => Regex.Replace(code, @"^([ \t]*\r?\n)+", "");

    private void EmitDefaultImpl(string typeName)
    {
        sb.Append($"impl Default for {typeName}");
        OpenBrace("");
        sb.Append($"{Indent1}fn default() -> Self");
        OpenBrace(Indent1);
        sb.AppendLine($"{Indent1}{Indent1}Self::new()");
        sb.AppendLine($"{Indent1}}}");
        sb.AppendLine("}");
    }

    /// <summary>
    /// Writes ` {` + newline (K&amp;R) or newline + `{` + newline (Allman) depending on style.
    /// </summary>
    private void OpenBrace(string indent)
    {
        if (codeStyleSettings.BracesOnNewLines)
        {
            sb.AppendLine();
            sb.AppendLine(indent + "{");
        }
        else
        {
            sb.AppendLine(" {");
        }
    }

    private void EmitConstructor(ConstructorDeclarationSyntax? ctor, List<(string name, string init)> fieldInits)
    {
        if (ctor != null)
        {
            if (ctor.ParameterList.Parameters.Any())
                throw new NotImplementedException("Rust transpiler: constructors with parameters are not supported.");
            if (ctor.Body != null && ctor.Body.Statements.Any())
                throw new NotImplementedException("Rust transpiler: constructors with statements are not supported (spike).");

            VisitLeadingTrivia(ctor.GetFirstToken());
        }
        else
        {
            sb.Append(Indent1);
        }

        string constText = ctorCanBeConst ? "const " : "";
        sb.Append($"pub {constText}fn new() -> Self");
        OpenBrace(Indent1);
        sb.Append($"{Indent1}{Indent1}Self");
        OpenBrace(Indent1 + Indent1);
        foreach (var (name, init) in fieldInits)
        {
            sb.AppendLine($"{Indent1}{Indent1}{Indent1}{name}: {init},");
        }
        sb.AppendLine($"{Indent1}{Indent1}}}");
        sb.AppendLine($"{Indent1}}}");
    }

    private void EmitVarsStruct(TypeDeclarationSyntax node)
    {
        var fieldInits = new List<(string name, string init)>();

        VisitLeadingTrivia(node.GetFirstToken());
        string indent = StringUtils.FindLastIndent(sb);

        var derives = renderConfigRust.VarsDerives.Trim();
        if (derives.Length > 0)
        {
            sb.AppendLine($"#[derive({derives})]");
            sb.Append(indent);
        }

        sb.Append($"pub struct {node.Identifier.Text}");
        OpenBrace(indent);

        foreach (var field in node.Members.OfType<FieldDeclarationSyntax>())
        {
            EmitStructField(field, fieldInits);
        }

        sb.AppendLine(indent + "}");
        sb.AppendLine();

        // const constructor
        sb.Append($"{indent}impl {node.Identifier.Text}");
        OpenBrace(indent);
        string constText = ctorCanBeConst ? "const " : "";
        sb.Append($"{indent}{Indent1}pub {constText}fn new() -> Self");
        OpenBrace(indent + Indent1);
        sb.Append($"{indent}{Indent1}{Indent1}Self");
        OpenBrace(indent + Indent1 + Indent1);
        foreach (var (name, init) in fieldInits)
        {
            sb.AppendLine($"{indent}{Indent1}{Indent1}{Indent1}{name}: {init},");
        }
        sb.AppendLine($"{indent}{Indent1}{Indent1}}}");
        sb.AppendLine($"{indent}{Indent1}}}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }

    /// <summary>
    /// User variable declarations are raw target code. For Rust we ask for `name: Type = const_init,`
    /// so that we can split them into a struct field and a const initializer.
    /// </summary>
    private static readonly Regex userVarRegex = new(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?<name>[A-Za-z_]\w*)\s*:\s*(?<type>[^=]+?)\s*(?:=\s*(?<init>.+?))?\s*[,;]?\s*(?<comment>//.*)?$");

    private void EmitStructField(FieldDeclarationSyntax field, List<(string name, string init)> fieldInits)
    {
        var variable = field.Declaration.Variables.Single();
        string name = variable.Identifier.Text;

        if (transpilerHelper.IsGilData(name))
        {
            VisitLeadingTrivia(field.GetFirstToken());
            string indent = StringUtils.FindLastIndent(sb);
            string userCode = GilTranspilerHelper.GetOriginalCodeFromGilFieldEcho(field).Trim();

            var match = userVarRegex.Match(userCode);
            if (userCode.StartsWith("//") || !match.Success)
            {
                sb.AppendLine(userCode); // pass through as is (comments, attributes, ...)
                return;
            }

            string userName = match.Groups["name"].Value;
            string type = match.Groups["type"].Value.Trim();
            string comment = match.Groups["comment"].Success ? " " + match.Groups["comment"].Value : "";
            sb.AppendLine($"pub {userName}: {type},{comment}");

            if (match.Groups["init"].Success)
            {
                fieldInits.Add((userName, match.Groups["init"].Value.Trim()));
            }
            else
            {
                ctorCanBeConst = false;
                fieldInits.Add((userName, "Default::default()"));
            }
            _ = indent;
            return;
        }

        VisitLeadingTrivia(field.GetFirstToken());

        bool isPublic = field.Modifiers.Any(SyntaxKind.PublicKeyword);
        sb.Append(isPublic ? "pub " : "");
        sb.Append(name);
        sb.Append(": ");
        sb.Append(RustType(field.Declaration.Type));
        sb.Append(',');
        VisitTrailingTrivia(field.GetLastToken());

        string init;
        if (variable.Initializer != null)
        {
            init = Capture(() => Visit(variable.Initializer.Value)).Trim();
        }
        else
        {
            init = DefaultValueFor(model.GetTypeInfo(field.Declaration.Type).Type.ThrowIfNull());
        }
        fieldInits.Add((name, init));
    }

    private string DefaultValueFor(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            var member = GetDefaultEnumMember((INamedTypeSymbol)type);
            return $"{type.Name}::{member.Name}";
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return "false";
            case SpecialType.System_String: return "\"\"";
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64: return "0";
            case SpecialType.System_Single:
            case SpecialType.System_Double: return "0.0";
        }

        if (type.TypeKind == TypeKind.Struct || type.TypeKind == TypeKind.Class)
        {
            return $"{type.Name}::new()";
        }

        ctorCanBeConst = false;
        return "Default::default()";
    }

    private static IFieldSymbol GetDefaultEnumMember(INamedTypeSymbol enumType)
    {
        var members = enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
        return members.FirstOrDefault(m => Convert.ToInt64(m.ConstantValue) == 0) ?? members.First();
    }

    private void EmitConst(FieldDeclarationSyntax field)
    {
        var variable = field.Declaration.Variables.Single();
        VisitLeadingTrivia(field.GetFirstToken());
        sb.Append($"pub const {variable.Identifier.Text}: {RustType(field.Declaration.Type)} = ");
        Visit(variable.Initializer.ThrowIfNull().Value);
        StringUtils.EraseTrailingWhitespace(sb);
        sb.Append(';');
        VisitTrailingTrivia(field.GetLastToken());
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var symbol = model.GetDeclaredSymbol(node).ThrowIfNull();
        var members = symbol.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
        long maxValue = members.Count == 0 ? 0 : members.Max(m => Convert.ToInt64(m.ConstantValue));
        string repr = maxValue <= byte.MaxValue ? "u8" : (maxValue <= ushort.MaxValue ? "u16" : "u32");

        VisitLeadingTrivia(node.GetFirstToken());
        string indent = StringUtils.FindLastIndent(sb);
        sb.AppendLine($"#[repr({repr})]");
        sb.AppendLine(indent + EnumDerives);
        sb.Append(indent);

        skipLeadingTrivia = true;
        base.VisitEnumDeclaration(node);

        string countName = RustNameMangler.ToSnakeCase(node.Identifier.Text).ToUpper() + "_COUNT";
        sb.AppendLine($"{indent}pub const {countName}: usize = {members.Count};");
    }

    public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node)
    {
        var symbol = model.GetDeclaredSymbol(node).ThrowIfNull();
        var defaultMember = GetDefaultEnumMember(symbol.ContainingType);

        if (SymbolEqualityComparer.Default.Equals(symbol, defaultMember))
        {
            VisitLeadingTrivia(node.GetFirstToken());
            string indent = StringUtils.FindLastIndent(sb);
            sb.AppendLine("#[default]");
            sb.Append(indent);
            skipLeadingTrivia = true;
        }

        base.VisitEnumMemberDeclaration(node);
    }

    // to ignore GIL attributes
    public override void VisitAttributeList(AttributeListSyntax node)
    {
        VisitLeadingTrivia(node.GetFirstToken());
    }

    //=====================================================================================
    // Methods
    //=====================================================================================

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (transpilerHelper.IsGilData(node))
            return;

        bool isStatic = node.Modifiers.Any(SyntaxKind.StaticKeyword);
        bool isPublic = node.Modifiers.Any(SyntaxKind.PublicKeyword);

        VisitLeadingTrivia(node.GetFirstToken());

        if (isPublic)
            sb.Append("pub ");

        // static GIL methods (state_id_to_string, ...) are pure lookups, so they can be `const fn`.
        if (isStatic)
            sb.Append("const ");

        sb.Append("fn ");
        sb.Append(node.Identifier.Text);
        sb.Append('(');

        var parts = new List<string>();
        if (!isStatic)
            parts.Add("&mut self");

        foreach (var p in node.ParameterList.Parameters)
        {
            parts.Add($"{p.Identifier.Text}: {RustType(p.Type.ThrowIfNull())}");
        }
        sb.Append(string.Join(", ", parts));
        sb.Append(')');

        string returnType = RustType(node.ReturnType);
        if (returnType.Length > 0)
        {
            sb.Append(" -> " + returnType);
        }

        VisitTrailingTrivia(node.ParameterList.CloseParenToken);
        Visit(node.Body.ThrowIfNull());
    }

    private static string RustType(TypeSyntax typeSyntax)
    {
        switch (typeSyntax)
        {
            case PredefinedTypeSyntax pts:
                switch ((SyntaxKind)pts.Keyword.RawKind)
                {
                    case SyntaxKind.VoidKeyword: return "";
                    case SyntaxKind.BoolKeyword: return "bool";
                    case SyntaxKind.StringKeyword: return "&'static str";
                    case SyntaxKind.ByteKeyword: return "u8";
                    case SyntaxKind.SByteKeyword: return "i8";
                    case SyntaxKind.ShortKeyword: return "i16";
                    case SyntaxKind.UShortKeyword: return "u16";
                    case SyntaxKind.IntKeyword: return "i32";
                    case SyntaxKind.UIntKeyword: return "u32";
                    case SyntaxKind.LongKeyword: return "i64";
                    case SyntaxKind.ULongKeyword: return "u64";
                    case SyntaxKind.FloatKeyword: return "f32";
                    case SyntaxKind.DoubleKeyword: return "f64";
                    case SyntaxKind.CharKeyword: return "char";
                }
                break;

            case IdentifierNameSyntax ins:
                return ins.Identifier.Text;

            case QualifiedNameSyntax qns:
                return qns.Right.Identifier.Text; // nested types are hoisted to module level
        }

        throw new NotImplementedException($"Rust transpiler: unsupported type `{typeSyntax}`.");
    }

    //=====================================================================================
    // Statements
    //=====================================================================================

    public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        var variable = node.Declaration.Variables.Single();

        VisitLeadingTrivia(node.GetFirstToken());
        sb.Append($"let mut {variable.Identifier.Text}: {RustType(node.Declaration.Type)}");

        if (variable.Initializer != null)
        {
            sb.Append(" = ");
            Visit(variable.Initializer.Value);
            StringUtils.EraseTrailingWhitespace(sb);
        }

        sb.Append(';');
        VisitTrailingTrivia(node.SemicolonToken);
    }

    public override void VisitIfStatement(IfStatementSyntax node)
    {
        VisitLeadingTrivia(node.GetFirstToken());

        if (node.Statement is not BlockSyntax)
            throw new NotImplementedException("Rust transpiler: `if` without braces is not supported.");

        // `if (true) { ... }` becomes a plain block.
        if (node.Condition.IsKind(SyntaxKind.TrueLiteralExpression) && node.Else == null)
        {
            skipLeadingTrivia = true; // the block's open brace
            Visit(node.Statement);
            return;
        }

        sb.Append("if ");
        Visit(StripParens(node.Condition));
        StringUtils.EraseTrailingWhitespace(sb);
        VisitConditionEndAndBlock(node.CloseParenToken, (BlockSyntax)node.Statement);

        if (node.Else != null)
        {
            VisitToken(node.Else.ElseKeyword);
            if (node.Else.Statement is not BlockSyntax and not IfStatementSyntax)
                throw new NotImplementedException("Rust transpiler: `else` without braces is not supported.");
            Visit(node.Else.Statement);
        }
    }

    private static ExpressionSyntax StripParens(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax pes)
            expression = pes.Expression;
        return expression;
    }

    public override void VisitWhileStatement(WhileStatementSyntax node)
    {
        VisitLeadingTrivia(node.GetFirstToken());
        sb.Append("while ");
        Visit(StripParens(node.Condition));
        StringUtils.EraseTrailingWhitespace(sb);
        if (node.Statement is not BlockSyntax block)
            throw new NotImplementedException("Rust transpiler: `while` without braces is not supported.");
        VisitConditionEndAndBlock(node.CloseParenToken, block);
    }

    /// <summary>
    /// GIL guards are written with the open brace on its own line even when `BracesOnNewLines` is false.
    /// In K&amp;R mode, pull the brace up (`if cond {`) when only whitespace separates it from the condition.
    /// </summary>
    private void VisitConditionEndAndBlock(SyntaxToken closeParen, BlockSyntax block)
    {
        bool onlyWhitespaceBetween = closeParen.TrailingTrivia.Concat(block.OpenBraceToken.LeadingTrivia)
            .All(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia));

        if (!codeStyleSettings.BracesOnNewLines && onlyWhitespaceBetween)
        {
            sb.Append(' ');
            skipLeadingTrivia = true;
        }
        else
        {
            VisitTrailingTrivia(closeParen);
        }

        Visit(block);
    }

    public override void VisitSwitchStatement(SwitchStatementSyntax node)
    {
        VisitLeadingTrivia(node.GetFirstToken());
        sb.Append("match ");
        Visit(StripParens(node.Expression));
        StringUtils.EraseTrailingWhitespace(sb);
        VisitTrailingTrivia(node.CloseParenToken);

        VisitToken(node.OpenBraceToken);

        bool coversAllEnumMembers = CoversAllEnumMembers(node);

        foreach (var section in node.Sections)
        {
            bool isDefault = section.Labels.Any(l => l is DefaultSwitchLabelSyntax);

            // A Rust enum can't hold an out of range value (that would already be UB), so a GIL `default:` that is only
            // there "to be safe" is an unreachable pattern when every variant has an arm. Skip it.
            if (isDefault && coversAllEnumMembers)
                continue;

            Visit(section);
        }

        // Rust requires exhaustive matches. GIL switches over enums usually cover every member, but not always.
        bool hasDefault = node.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
        if (!hasDefault && !coversAllEnumMembers)
        {
            string indent = FindIndentOfLine(node.CloseBraceToken);
            sb.AppendLine(indent + Indent1 + "_ => {}");
        }

        VisitToken(node.CloseBraceToken);
    }

    private bool CoversAllEnumMembers(SwitchStatementSyntax node)
    {
        var type = model.GetTypeInfo(node.Expression).Type;
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            return false;

        var memberCount = enumType.GetMembers().OfType<IFieldSymbol>().Count(f => f.HasConstantValue);
        var covered = new HashSet<string>();
        foreach (var label in node.Sections.SelectMany(s => s.Labels).OfType<CaseSwitchLabelSyntax>())
        {
            var symbol = model.GetSymbolInfo(label.Value).Symbol;
            if (symbol != null)
                covered.Add(symbol.Name);
        }

        return covered.Count >= memberCount;
    }

    private static string FindIndentOfLine(SyntaxToken token)
    {
        var leading = token.LeadingTrivia.ToFullString();
        int lastNewLine = leading.LastIndexOf('\n');
        var tail = lastNewLine >= 0 ? leading[(lastNewLine + 1)..] : leading;
        return Regex.Match(tail, @"^[ \t]*").Value;
    }

    /// <summary>
    /// `case X: a(); break;` becomes `X => { a(); }`. The trailing `break` is dropped (Rust match arms don't fall through,
    /// and a `break` here would break out of an enclosing loop instead).
    /// </summary>
    public override void VisitSwitchSection(SwitchSectionSyntax node)
    {
        if (node.Labels.Count != 1)
            throw new NotImplementedException("Rust transpiler: switch sections with multiple labels are not supported (spike).");

        var label = node.Labels.Single();

        // the C# `break;` that ends the section. Any other `break` would be a GIL construct we don't expect.
        var statements = node.Statements.ToList();
        BreakStatementSyntax? endingBreak = statements.LastOrDefault() as BreakStatementSyntax;
        if (endingBreak != null)
            statements.RemoveAt(statements.Count - 1);

        if (statements.SelectMany(s => s.DescendantNodesAndSelf()).OfType<BreakStatementSyntax>().Any(b => !b.Ancestors().TakeWhile(a => a != node).Any(a => a is WhileStatementSyntax or ForStatementSyntax or DoStatementSyntax or SwitchSectionSyntax)))
            throw new NotImplementedException("Rust transpiler: `break` in the middle of a switch section is not supported.");

        // label
        VisitLeadingTrivia(label.GetFirstToken());
        string indent = StringUtils.FindLastIndent(sb);
        if (label is CaseSwitchLabelSyntax caseLabel)
        {
            Visit(caseLabel.Value);
            StringUtils.EraseTrailingWhitespace(sb);
        }
        else
        {
            sb.Append('_');
        }
        sb.Append(" =>");

        bool singleLine = !label.ColonToken.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));

        if (singleLine)
        {
            sb.Append(" { ");
            foreach (var statement in statements)
            {
                string code = Capture(() => Visit(statement));
                if (endingBreak == null && statement == statements.Last())
                {
                    // section's trailing trivia (like `// comment`) is output after the closing brace below
                    string trailing = statement.GetLastToken().TrailingTrivia.ToFullString();
                    if (code.EndsWith(trailing))
                        code = code[..^trailing.Length];
                }
                sb.Append(code.Trim());
                sb.Append(' ');
            }
            sb.Append('}');

            var lastToken = node.GetLastToken();
            VisitTrailingTrivia(lastToken); // includes any `// comment` and the new line
            return;
        }

        // multi line
        OpenBrace(indent);
        foreach (var statement in statements)
        {
            Visit(statement);
        }

        if (endingBreak != null)
        {
            // keep comments that were attached to the dropped break, like `// No events handled by this state`
            string breakLeading = Capture(() => VisitLeadingTrivia(endingBreak.GetFirstToken()));
            breakLeading = Regex.Replace(breakLeading, @"[ \t]+$", "");
            sb.Append(breakLeading);
            var comment = endingBreak.GetLastToken().TrailingTrivia.Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)).Select(t => t.ToString()).FirstOrDefault();
            if (comment != null)
                sb.AppendLine(indent + Indent1 + comment);
        }

        StringUtils.EraseTrailingWhitespace(sb);
        sb.AppendLine();
        sb.AppendLine(indent + "}");
    }

    /// <summary>
    /// With `BracesOnNewLines == false`, GIL puts the open brace of a bare block that follows a comment line at the start of a
    /// line with a single space of indentation. Re-indent it to match its closing brace.
    /// </summary>
    public override void VisitBlock(BlockSyntax node)
    {
        var open = node.OpenBraceToken;
        var leading = open.LeadingTrivia;
        bool braceStartsLine = leading.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia) || t.IsKind(SyntaxKind.SingleLineCommentTrivia))
            || open.GetPreviousToken().TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));

        if (braceStartsLine && !skipLeadingTrivia)
        {
            VisitLeadingTriviaUpToLastNewLine(open); // comments before the brace, if any
            sb.Append(FindIndentOfLine(node.CloseBraceToken));
            skipLeadingTrivia = true;
        }

        base.VisitBlock(node);
    }

    private void VisitLeadingTriviaUpToLastNewLine(SyntaxToken token)
    {
        var list = token.LeadingTrivia.ToList();
        int lastEol = list.FindLastIndex(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        for (int i = 0; i <= lastEol; i++)
        {
            VisitTrivia(list[i]);
        }
    }

    public override void VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (transpilerHelper.HandleGilSpecialExpressionStatements(node, sb))
            return;

        if (node.Expression is PostfixUnaryExpressionSyntax post && (post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression)))
        {
            VisitLeadingTrivia(node.GetFirstToken());
            skipLeadingTrivia = true;
            Visit(post.Operand);
            StringUtils.EraseTrailingWhitespace(sb);
            sb.Append(post.IsKind(SyntaxKind.PostIncrementExpression) ? " += 1;" : " -= 1;");
            VisitTrailingTrivia(node.SemicolonToken);
            return;
        }

        base.VisitExpressionStatement(node);
    }

    //=====================================================================================
    // Expressions
    //=====================================================================================

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        bool done = false;

        done |= transpilerHelper.HandleGilSpecialInvocations(node, sb);
        done |= transpilerHelper.HandleGilUnusedVarSpecialInvocation(node, argument =>
        {
            // skip parent expression semicolon and end of line trivia
            ExpressionStatementSyntax expressionParent = (ExpressionStatementSyntax)node.Parent!;
            tokenToSkip = expressionParent.SemicolonToken;
        });

        if (!done)
        {
            base.VisitInvocationExpression(node);
        }
    }

    /// <summary>
    /// `StateId.ROOT` to `StateId::ROOT`. `this.foo` to `self.foo`.
    /// </summary>
    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var leftSymbol = model.GetSymbolInfo(node.Expression).Symbol;

        if (leftSymbol is INamedTypeSymbol)
        {
            // Nested types are hoisted to module level, so `Sm.StateId.X` must become `StateId::X`.
            if (node.Expression is MemberAccessExpressionSyntax inner && model.GetSymbolInfo(inner.Expression).Symbol is INamedTypeSymbol outer && SymbolEqualityComparer.Default.Equals(outer, smClassSymbol))
            {
                VisitLeadingTrivia(node.GetFirstToken());
                sb.Append(inner.Name.Identifier.Text);
            }
            else if (SymbolEqualityComparer.Default.Equals(leftSymbol, smClassSymbol))
            {
                VisitLeadingTrivia(node.GetFirstToken());
                sb.Append("Self");
            }
            else
            {
                Visit(node.Expression);
            }
            sb.Append("::");
            Visit(node.Name);
            return;
        }

        base.VisitMemberAccessExpression(node);
    }

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        bool isMemberAccessName = node.Parent is MemberAccessExpressionSyntax maes && maes.Name == node;

        if (!isMemberAccessName && smClassSymbol != null)
        {
            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol != null && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, smClassSymbol) && !transpilerHelper.IsGilData(symbol.Name))
            {
                if (symbol is IMethodSymbol { IsStatic: true })
                {
                    VisitLeadingTrivia(node.GetFirstToken());
                    sb.Append("Self::" + node.Identifier.Text);
                    VisitTrailingTrivia(node.GetLastToken());
                    return;
                }

                if (symbol is IMethodSymbol || symbol is IFieldSymbol { IsStatic: false, IsConst: false })
                {
                    VisitLeadingTrivia(node.GetFirstToken());
                    sb.Append("self." + node.Identifier.Text);
                    VisitTrailingTrivia(node.GetLastToken());
                    return;
                }
            }
        }

        base.VisitIdentifierName(node);
    }

    /// <summary>
    /// `(int)x` to `(x as i32)`
    /// </summary>
    public override void VisitCastExpression(CastExpressionSyntax node)
    {
        VisitLeadingTrivia(node.GetFirstToken());
        sb.Append('(');
        Visit(node.Expression);
        StringUtils.EraseTrailingWhitespace(sb);
        sb.Append(" as ");
        sb.Append(RustType(node.Type));
        sb.Append(')');
        VisitTrailingTrivia(node.GetLastToken());
    }

    /// <summary>
    /// `new Vars()` to `Vars::new()`
    /// </summary>
    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        VisitLeadingTrivia(node.GetFirstToken());
        sb.Append(RustType(node.Type));
        sb.Append("::new");
        if (node.ArgumentList != null)
        {
            Visit(node.ArgumentList);
        }
        else
        {
            sb.Append("()");
        }
    }

    public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.SuppressNullableWarningExpression))
        {
            Visit(node.Operand);
            return;
        }

        throw new NotImplementedException($"Rust transpiler: unsupported postfix expression `{node}` (only supported as a statement).");
    }

    public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
        VisitLeadingTrivia(node.GetFirstToken());
        sb.Append("if ");
        Visit(node.Condition);
        StringUtils.EraseTrailingWhitespace(sb);
        sb.Append(" { ");
        Visit(node.WhenTrue);
        StringUtils.EraseTrailingWhitespace(sb);
        sb.Append(" } else { ");
        Visit(node.WhenFalse);
        StringUtils.EraseTrailingWhitespace(sb);
        sb.Append(" }");
    }

    //=====================================================================================
    // Tokens & trivia
    //=====================================================================================

    public override void VisitToken(SyntaxToken token)
    {
        if (token == tokenToSkip)
        {
            tokenToSkip = null;
            return;
        }

        if (skipLeadingTrivia)
        {
            skipLeadingTrivia = false;
        }
        else
        {
            token.LeadingTrivia.VisitWith(this);
        }

        string tokenText = token.Text;

        switch ((SyntaxKind)token.RawKind)
        {
            case SyntaxKind.ThisKeyword: tokenText = "self"; break;
            case SyntaxKind.PublicKeyword: tokenText = "pub"; break;
            case SyntaxKind.NullKeyword: throw new NotImplementedException("Rust transpiler: `null` is not supported.");
        }

        if (token.IsKind(SyntaxKind.ExclamationToken) && token.Parent.IsKind(SyntaxKind.SuppressNullableWarningExpression))
        {
            // ignore
        }
        else
        {
            sb.Append(tokenText);
        }

        token.TrailingTrivia.VisitWith(this);
    }

    public override void VisitTrivia(SyntaxTrivia trivia)
    {
        sb.Append(trivia.ToString());
    }

    private string Capture(Action action)
    {
        var originalSb = sb;
        sb = new StringBuilder();
        action();
        var captured = sb.ToString();
        sb = originalSb;
        return captured;
    }
}
