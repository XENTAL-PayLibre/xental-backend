namespace Xental.Application.Common.Interfaces;

/// <summary>
/// Verifies the Nomba webhook signature. Nomba computes Base64(HMAC-SHA256(secret, payload))
/// where payload is a colon-delimited concatenation of nine fields (eight from the body plus
/// the <c>nomba-timestamp</c> header) — see the implementation. Implemented in Infrastructure
/// where the secret lives.
/// </summary>
public interface INombaSignatureVerifier
{
    bool Verify(byte[] rawBody, string? signatureHeader, string? timestampHeader);
}
