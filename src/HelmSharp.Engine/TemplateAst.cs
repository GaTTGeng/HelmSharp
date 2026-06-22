namespace HelmSharp.Engine;

/// <summary>
/// Base class for all AST nodes in a parsed Helm/Go template.
/// </summary>
public abstract class TemplateNode
{
    /// <summary>Byte offset where this node starts in the original template.</summary>
    public int StartOffset { get; set; }

    /// <summary>Byte offset where this node ends in the original template.</summary>
    public int EndOffset { get; set; }

    /// <summary>Starting line number (1-based).</summary>
    public int StartLine { get; set; }

    /// <summary>Ending line number (1-based).</summary>
    public int EndLine { get; set; }
}

/// <summary>
/// The root of a parsed template. Contains a flat list of top-level nodes
/// (text, actions, blocks, defines, comments).
/// </summary>
public sealed class TemplateDocumentNode : TemplateNode
{
    public List<TemplateNode> Children { get; init; } = new();
}

/// <summary>Raw text content between template actions.</summary>
public sealed class TextNode : TemplateNode
{
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// A single action expression inside delimiters (<c>{{ expr }}</c>).
/// The expression is stored as a raw string for evaluation by the existing engine.
/// </summary>
public sealed class ActionNode : TemplateNode
{
    /// <summary>The raw expression text (without delimiters or trim markers).</summary>
    public string Expression { get; init; } = string.Empty;

    /// <summary>True if the opening delimiter had a trim marker (<c>{{-</c>).</summary>
    public bool LeftTrim { get; set; }

    /// <summary>True if the closing delimiter had a trim marker (<c>-}}</c>).</summary>
    public bool RightTrim { get; set; }
}

/// <summary>A comment action (<c>{{/* ... */}}</c>). Produces no output.</summary>
public sealed class CommentNode : TemplateNode
{
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// A block node like <c>if</c>, <c>with</c>, or <c>range</c>.
/// Contains the condition expression, true body, optional else-if chain, and optional else body.
/// </summary>
public sealed class BlockNode : TemplateNode
{
    /// <summary>The block keyword: <c>"if"</c>, <c>"with"</c>, or <c>"range"</c>.</summary>
    public string Keyword { get; init; } = string.Empty;

    /// <summary>The raw expression text that follows the keyword.</summary>
    public string Expression { get; init; } = string.Empty;

    /// <summary>True if the opening delimiter had a trim marker.</summary>
    public bool LeftTrim { get; set; }

    /// <summary>True if the closing delimiter had a trim marker.</summary>
    public bool RightTrim { get; set; }

    /// <summary>The true branch body.</summary>
    public TemplateNode TrueBody { get; set; } = new TemplateDocumentNode();

    /// <summary>The false branch body (for <c>else</c> or empty).</summary>
    public TemplateNode FalseBody { get; set; } = new TemplateDocumentNode();

    /// <summary>
    /// Chain of <c>else if</c> conditions with their bodies.
    /// Each entry represents one <c>else if condition</c> + body segment.
    /// </summary>
    public List<ElseIfBranch> ElseIfChain { get; init; } = new();
}

/// <summary>A single <c>else if condition</c> with its body.</summary>
public sealed class ElseIfBranch
{
    /// <summary>The raw condition expression text.</summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>The body nodes for this else-if branch.</summary>
    public TemplateNode Body { get; set; } = new TemplateDocumentNode();

    /// <summary>True if the else-if delimiter had a trim marker.</summary>
    public bool TrimMarker { get; set; }
}

/// <summary>
/// A <c>define "name"</c> ... <c>end</c> block.
/// Extracted during parsing and stored in the global define registry.
/// </summary>
public sealed class DefineNode : TemplateNode
{
    /// <summary>The template name (without quotes).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The body of the define block.</summary>
    public TemplateNode Body { get; set; } = new TemplateDocumentNode();

    /// <summary>True if the opening delimiter had a trim marker.</summary>
    public bool LeftTrim { get; set; }

    /// <summary>True if the closing delimiter of the opening tag had a trim marker.</summary>
    public bool RightTrim { get; set; }
}
