namespace Xental.Application.Common.Interfaces;

/// <summary>
/// Object storage for KYC/KYB documents. Backed by MinIO today and AWS S3 later — the same S3 API,
/// so switching is configuration only. Private bucket; documents are retrieved only via short-TTL
/// presigned URLs. Implemented in Infrastructure; a fake is used in tests.
/// </summary>
public interface IDocumentStorage
{
    /// <summary>Store an object and return its storage key.</summary>
    Task PutAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>A short-lived presigned URL an admin/UI can use to view the object.</summary>
    Task<Uri> CreateDownloadUrlAsync(string objectKey, TimeSpan ttl, CancellationToken ct = default);
}
