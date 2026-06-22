namespace HelmSharp.Engine;

/// <summary>
/// Provides token evaluation and rendering to extracted function helpers.
/// Decouples function implementations from the concrete HelmTemplateRenderer.
/// </summary>
internal interface IEvaluationContext
{
    object? EvaluateToken(string? token, TemplateContext context);
    string RenderSection(string template, TemplateContext context);
}
