namespace HelmSharp.Engine;

/// <summary>
/// The managed renderer encountered a Helm template feature that it does not implement.
/// </summary>
/// <remarks>
/// This is a rendering diagnostic, rather than a platform-capability exception. It is
/// collected with the template path by <see cref="HelmTemplateRenderer.Render"/>.
/// </remarks>
public sealed class UnsupportedTemplateFeatureException : Exception
{
    /// <summary>Creates the exception describing the unimplemented feature.</summary>
    /// <param name="message">Which feature was unsupported and the construct that triggered it.</param>
    public UnsupportedTemplateFeatureException(string message)
        : base(message)
    {
    }
}
