namespace Xental.Infrastructure.Storage;

/// <summary>
/// Object-storage config. MinIO now, S3 later — same S3 API, so switching is config only:
/// for MinIO set Endpoint + ForcePathStyle; for S3 drop Endpoint and set Region.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "minio";     // minio | s3
    public string Endpoint { get; set; } = string.Empty; // e.g. http://minio:9000 (blank for real S3)
    public string Bucket { get; set; } = "xental-kyc-docs";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";

    public bool IsMinio => Provider.Equals("minio", StringComparison.OrdinalIgnoreCase);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);
}
