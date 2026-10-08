namespace HelmSharp.Engine;

/// <summary>
/// Provides token evaluation and rendering to extracted function helpers.
/// Decouples function implementations from the concrete HelmTemplateRenderer.
/// </summary>
internal interface IEvaluationContext
{
    /// <summary>
    /// Evaluates a single template token (identifier, field path, or literal) to its raw value.
    /// A null or empty token evaluates to the current dot scope.
    /// </summary>
    object? EvaluateToken(string? token, TemplateContext context);
    /// <summary>
    /// Renders a raw template fragment (for example an <c>include</c> body or captured block)
    /// to its output text under the given context.
    /// </summary>
    string RenderSection(string template, TemplateContext context);
}
