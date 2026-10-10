namespace HelmSharp.Action;

/// <summary>
/// Outcome of an authenticating <c>.prov</c> verification: OpenPGP signature validity,
/// signer identity, trusted-signer match, and chart-digest agreement.
/// Authenticity requires all of <see cref="SignatureValid"/>, <see cref="SignerTrusted"/>,
/// and <see cref="DigestMatches"/>; <see cref="IsValid"/> reports the conjunction.
/// </summary>
public sealed class HelmProvenanceVerificationResult
{
    internal HelmProvenanceVerificationResult(
        bool signatureValid,
        bool signerTrusted,
        bool digestMatches,
        string? signerFingerprint,
        string? signerKeyId,
        string? signerUserId,
        string? expectedSha256,
        string? actualSha256,
        string? failureReason)
    {
        SignatureValid = signatureValid;
        SignerTrusted = signerTrusted;
        DigestMatches = digestMatches;
        SignerFingerprint = signerFingerprint;
        SignerKeyId = signerKeyId;
        SignerUserId = signerUserId;
        ExpectedSha256 = expectedSha256;
        ActualSha256 = actualSha256;
        FailureReason = failureReason;
    }

    /// <summary>
    /// True when the OpenPGP signature over the signed provenance body is cryptographically valid.
    /// This alone does not establish authenticity — the signer must also be trusted.
    /// </summary>
    public bool SignatureValid { get; }

    /// <summary>True when the signing key matches one of the caller-supplied trusted keys.</summary>
    public bool SignerTrusted { get; }

    /// <summary>True when the chart archive SHA-256 matches the signed <c>files:</c> digest.</summary>
    public bool DigestMatches { get; }

    /// <summary>True when the signature is valid, the signer is trusted, and the digest matches.</summary>
    public bool IsValid => SignatureValid && SignerTrusted && DigestMatches;

    /// <summary>Hex fingerprint of the key that produced the signature, when it could be determined.</summary>
    public string? SignerFingerprint { get; }

    /// <summary>Hex key ID of the signing key, when present.</summary>
    public string? SignerKeyId { get; }

    /// <summary>Primary user ID of the signing key, when present.</summary>
    public string? SignerUserId { get; }

    /// <summary>SHA-256 recorded in the signed <c>files:</c> section.</summary>
    public string? ExpectedSha256 { get; }

    /// <summary>SHA-256 computed over the chart archive bytes.</summary>
    public string? ActualSha256 { get; }

    /// <summary>Human-readable reason for the first failed check, or null when <see cref="IsValid"/>.</summary>
    public string? FailureReason { get; }
}
