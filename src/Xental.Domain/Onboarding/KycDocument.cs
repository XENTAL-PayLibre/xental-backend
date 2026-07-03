using Xental.Domain.Common;

namespace Xental.Domain.Onboarding;

public enum KycDocumentType { CertificateOfIncorporation = 1, ProofOfAddress = 2 }

public enum DocumentReviewStatus { Pending = 1, Accepted = 2, Rejected = 3 }

/// <summary>
/// A KYB document. The bytes live in object storage (MinIO/S3); this row holds only the object key,
/// a content hash (tamper-evidence), and review state. Reviewed by an admin — never auto-approved.
/// </summary>
public sealed class KycDocument : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public KycDocumentType Type { get; private set; }
    public string ObjectKey { get; private set; } = null!;   // storage key (MinIO/S3)
    public string ContentHash { get; private set; } = null!;  // SHA-256 hex
    public string ContentType { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public DocumentReviewStatus ReviewStatus { get; private set; }

    private KycDocument() { } // EF

    public KycDocument(Guid tenantId, KycDocumentType type, string objectKey, string contentHash, string contentType, long sizeBytes)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        Type = type;
        ObjectKey = DomainException.Require(objectKey, nameof(objectKey));
        ContentHash = DomainException.Require(contentHash, nameof(contentHash));
        ContentType = DomainException.Require(contentType, nameof(contentType));
        if (sizeBytes <= 0) throw new DomainException("Document size must be positive.");
        SizeBytes = sizeBytes;
        ReviewStatus = DocumentReviewStatus.Pending;
    }

    public void MarkReviewed(bool accepted) =>
        ReviewStatus = accepted ? DocumentReviewStatus.Accepted : DocumentReviewStatus.Rejected;
}
