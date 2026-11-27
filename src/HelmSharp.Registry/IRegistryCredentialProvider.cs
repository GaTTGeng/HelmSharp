namespace HelmSharp.Registry;

/// <summary>Looks up credentials for a single normalized registry authority.</summary>
public interface IRegistryCredentialProvider
{
    /// <summary>Returns the credential for the exact authority, or null when none exists.</summary>
    Task<RegistryCredential?> GetAsync(string authority, CancellationToken cancellationToken);
}
