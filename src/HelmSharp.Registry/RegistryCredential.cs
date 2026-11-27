namespace HelmSharp.Registry;

/// <summary>Username and password or access token for one registry.</summary>
public sealed class RegistryCredential
{
    /// <summary>Creates an in-memory credential without persisting it.</summary>
    public RegistryCredential(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException("A registry credential requires both a username and a secret.");

        Username = username;
        Password = password;
    }

    public string Username { get; }

    /// <summary>Secret used for registry authentication.</summary>
    public string Password { get; }
}
