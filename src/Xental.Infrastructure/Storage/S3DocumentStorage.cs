using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Storage;

/// <summary>
/// Document storage over the S3 API — works unchanged against MinIO (dev/self-hosted) and AWS S3.
/// The bucket is private; documents are fetched only via short-TTL presigned URLs. The same client
/// code serves both backends; only <see cref="StorageOptions"/> differs.
/// </summary>
public sealed class S3DocumentStorage : IDocumentStorage
{
    private readonly IAmazonS3 _s3;
    private readonly StorageOptions _options;
    private readonly bool _disablePayloadSigning;

    public S3DocumentStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;
        var config = new AmazonS3Config { AuthenticationRegion = _options.Region };
        var endpointIsHttps = true; // real S3 is always HTTPS
        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            // MinIO / S3-compatible: talk to the custom endpoint with path-style addressing.
            config.ServiceURL = _options.Endpoint;
            config.ForcePathStyle = true;
            endpointIsHttps = _options.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_options.Region);
        }

        // Skipping chunked payload signing helps some S3-compatible stores, but the AWS SDK only
        // permits it over HTTPS. On a plain-HTTP endpoint (e.g. in-cluster MinIO) fall back to normal
        // signing — otherwise the signer throws "must be sent over HTTPS".
        _disablePayloadSigning = _options.IsMinio && endpointIsHttps;

        _s3 = new AmazonS3Client(_options.AccessKey, _options.SecretKey, config);
    }

    private int _bucketEnsured;

    public async Task PutAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false, // the caller owns the stream's lifetime; don't dispose it here
            DisablePayloadSigning = _disablePayloadSigning,
        }, ct);
    }

    /// <summary>Create the bucket on first use for MinIO (self-hosted). Real S3 buckets are pre-provisioned.</summary>
    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (!_options.IsMinio || Interlocked.Exchange(ref _bucketEnsured, 1) == 1) return;
        try
        {
            if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_s3, _options.Bucket))
                await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _options.Bucket }, ct);
        }
        catch { _bucketEnsured = 0; throw; } // allow a retry on the next call if it failed
    }

    public async Task<Uri> CreateDownloadUrlAsync(string objectKey, TimeSpan ttl, CancellationToken ct = default)
    {
        var url = await _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ttl),
        });
        return new Uri(url);
    }
}
