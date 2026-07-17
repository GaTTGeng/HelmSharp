namespace HelmSharp.Release;

public sealed class HelmReleaseStoreException : Exception
{
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

    public string SecretName { get; }
    public string NamespaceName { get; }
    public string Format { get; }
}
