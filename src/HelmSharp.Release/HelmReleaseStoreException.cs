namespace HelmSharp.Release;

/// <summary>
/// Thrown when a release record stored in a Kubernetes Secret cannot be decoded.
/// The message includes the Secret identity and payload format to make corrupt
/// release state diagnosable.
/// </summary>
public sealed class HelmReleaseStoreException : Exception
{
    /// <summary>Creates the exception for an unreadable release payload.</summary>
    /// <param name="secretName">Name of the Secret holding the payload.</param>
    /// <param name="namespaceName">Namespace of the Secret.</param>
    /// <param name="format">Payload format that failed to decode (for example <c>Helm v3 release</c>).</param>
    /// <param name="message">Decoder error describing what was unreadable.</param>
    /// <param name="innerException">The decoding failure, when available.</param>
    public HelmReleaseStoreException(
        string secretName,
        string namespaceName,
        string format,
        string message,
        Exception? innerException = null)
        : base($"Release Secret {namespaceName}/{secretName} contains an unreadable {format} payload: {message}", innerException)
    {
        SecretName = secretName;
        NamespaceName = namespaceName;
        Format = format;
    }

    /// <summary>Name of the Secret holding the unreadable payload.</summary>
    public string SecretName { get; }
    /// <summary>Namespace of the Secret.</summary>
    public string NamespaceName { get; }
    /// <summary>Payload format that failed to decode.</summary>
    public string Format { get; }
}
