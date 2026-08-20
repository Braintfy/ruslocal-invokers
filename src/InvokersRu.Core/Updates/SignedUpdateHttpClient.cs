using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace InvokersRu.Core.Updates
{
    public sealed class SignedUpdateArtifactDownload
    {
        internal SignedUpdateArtifactDownload(Uri finalUri, long bytesWritten, string sha256)
        {
            FinalUri = finalUri;
            BytesWritten = bytesWritten;
            Sha256 = sha256;
        }

        public Uri FinalUri { get; }
        public long BytesWritten { get; }
        public string Sha256 { get; }
    }

    /// <summary>
    /// A bounded GET-only transport for signed update metadata and catalog artifacts. It never follows an
    /// automatic redirect, decompresses HTTP content, starts a process, or writes outside a caller-owned stream.
    /// </summary>
    public sealed class SignedUpdateHttpClient : IDisposable
    {
        private const int MaxRedirects = 5;
        private const int BufferBytes = 64 * 1024;
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

        private readonly HttpClient _client;
        private readonly TimeSpan _operationTimeout;
        private bool _disposed;

        public SignedUpdateHttpClient()
            : this(CreateSecureHandler())
        {
        }

        internal SignedUpdateHttpClient(HttpMessageHandler handler, TimeSpan? operationTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _operationTimeout = operationTimeout ?? DefaultTimeout;
            if (_operationTimeout <= TimeSpan.Zero || _operationTimeout > TimeSpan.FromMinutes(5))
            {
                throw new ArgumentOutOfRangeException(nameof(operationTimeout), "Signed-update HTTP timeout must be between zero and five minutes.");
            }

            _client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        internal static HttpClientHandler CreateSecureHandler()
        {
            return new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                CheckCertificateRevocationList = true,
                MaxResponseHeadersLength = 64,
                PreAuthenticate = false,
                UseCookies = false,
                UseDefaultCredentials = false
            };
        }

        public Task<byte[]> DownloadEnvelopeAsync(string envelopeUrl, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            Uri initialUri = SignedUpdateUrlPolicy.ValidateEnvelopeUrl(envelopeUrl);
            return RunWithDeadlineAsync(
                token => DownloadEnvelopeCoreAsync(initialUri, token),
                cancellationToken);
        }

        private async Task<byte[]> DownloadEnvelopeCoreAsync(Uri initialUri, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendWithValidatedRedirectsAsync(initialUri, cancellationToken).ConfigureAwait(false);
            EnsureSuccessWithoutContentEncoding(response, "Signed update envelope");
            if (response.Content.Headers.ContentLength is long contentLength
                && (contentLength < 1 || contentLength > SignedUpdateLimits.MaxEnvelopeBytes))
            {
                throw new InvalidDataException("Signed update envelope Content-Length exceeds its fixed cap.");
            }

            using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var destination = new MemoryStream(capacity: Math.Min(
                SignedUpdateLimits.MaxEnvelopeBytes,
                response.Content.Headers.ContentLength is long length ? checked((int)length) : 16 * 1024));
            StreamCopyResult copied = await CopyCappedAsync(
                source,
                destination,
                SignedUpdateLimits.MaxEnvelopeBytes,
                exactBytes: null,
                cancellationToken).ConfigureAwait(false);
            if (copied.BytesWritten < 2)
            {
                throw new InvalidDataException("Signed update envelope is empty.");
            }

            return destination.ToArray();
        }

        public Task<SignedUpdateArtifactDownload> DownloadCatalogAsync(
            VerifiedSignedUpdate verified,
            SignedUpdateStateStore stateStore,
            Stream emptyDestination,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(verified);
            ArgumentNullException.ThrowIfNull(stateStore);
            ArgumentNullException.ThrowIfNull(emptyDestination);
            if (!emptyDestination.CanWrite || !emptyDestination.CanSeek
                || emptyDestination.Position != 0 || emptyDestination.Length != 0)
            {
                throw new ArgumentException("Catalog destination must be an empty writable seekable staging stream.", nameof(emptyDestination));
            }

            stateStore.AssertCurrentForRemoteArtifact(verified);
            return RunWithDeadlineAsync(
                token => DownloadCatalogCoreAsync(verified, stateStore, emptyDestination, token),
                cancellationToken);
        }

        private async Task<SignedUpdateArtifactDownload> DownloadCatalogCoreAsync(
            VerifiedSignedUpdate verified,
            SignedUpdateStateStore stateStore,
            Stream emptyDestination,
            CancellationToken cancellationToken)
        {
            VerifiedSignedUpdateCatalog catalog = verified.Manifest.Catalog;
            Uri initialUri = SignedUpdateUrlPolicy.ValidateCatalogUrl(catalog.Url, verified.Manifest.ReleaseId);
            try
            {
                using HttpResponseMessage response = await SendWithValidatedRedirectsAsync(initialUri, cancellationToken).ConfigureAwait(false);
                EnsureSuccessWithoutContentEncoding(response, "Signed update catalog");
                if (response.Content.Headers.ContentLength is long contentLength
                    && contentLength != catalog.CompressedBytes)
                {
                    throw new InvalidDataException("Signed update catalog Content-Length does not match its signed byte count.");
                }

                using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                StreamCopyResult copied = await CopyCappedAsync(
                    source,
                    emptyDestination,
                    Math.Min(catalog.CompressedBytes, SignedUpdateLimits.MaxCompressedCatalogBytes),
                    catalog.CompressedBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!FixedHashEquals(copied.Sha256, catalog.CompressedSha256))
                {
                    throw new InvalidDataException("Downloaded signed-update catalog SHA-256 does not match the signed manifest.");
                }

                await emptyDestination.FlushAsync(cancellationToken).ConfigureAwait(false);
                stateStore.AssertCurrentForRemoteArtifact(verified);
                Uri finalUri = response.RequestMessage?.RequestUri
                    ?? throw new InvalidDataException("HTTP response did not retain its validated final URI.");
                SignedUpdateUrlPolicy.ValidateArtifactResponseUrl(finalUri.AbsoluteUri);
                return new SignedUpdateArtifactDownload(finalUri, copied.BytesWritten, copied.Sha256);
            }
            catch
            {
                emptyDestination.SetLength(0);
                emptyDestination.Position = 0;
                throw;
            }
        }

        private async Task<T> RunWithDeadlineAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken callerToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            deadline.CancelAfter(_operationTimeout);
            try
            {
                return await operation(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!callerToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException("Signed-update HTTP operation exceeded its fixed end-to-end deadline.", exception);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _client.Dispose();
        }

        private async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
            Uri initialUri,
            CancellationToken cancellationToken)
        {
            Uri currentUri = initialUri;
            for (int redirectCount = 0; ; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                request.Headers.Accept.ParseAdd("application/octet-stream, application/json;q=0.9");
                HttpResponseMessage response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                try
                {
                    Uri responseUri = response.RequestMessage?.RequestUri
                        ?? throw new InvalidDataException("HTTP response did not retain its request URI.");
                    SignedUpdateUrlPolicy.ValidateArtifactResponseUrl(responseUri.AbsoluteUri);
                    if (!IsRedirect(response.StatusCode)) return response;
                    if (redirectCount >= MaxRedirects)
                    {
                        throw new InvalidDataException("Signed-update download exceeded its fixed redirect limit.");
                    }

                    Uri? location = response.Headers.Location;
                    if (location == null) throw new InvalidDataException("Signed-update redirect is missing Location.");
                    Uri nextUri = location.IsAbsoluteUri ? location : new Uri(responseUri, location);
                    currentUri = SignedUpdateUrlPolicy.ValidateArtifactResponseUrl(nextUri.AbsoluteUri);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }

                response.Dispose();
            }
        }

        private static void EnsureSuccessWithoutContentEncoding(HttpResponseMessage response, string label)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException($"{label} returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            if (response.Content.Headers.ContentEncoding.Count != 0)
            {
                throw new InvalidDataException($"{label} must not use transparent HTTP Content-Encoding.");
            }
        }

        private static async Task<StreamCopyResult> CopyCappedAsync(
            Stream source,
            Stream destination,
            long maximumBytes,
            long? exactBytes,
            CancellationToken cancellationToken)
        {
            if (maximumBytes < 1) throw new InvalidDataException("Signed-update download has an invalid byte cap.");
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            try
            {
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (total > maximumBytes - read)
                    {
                        throw new InvalidDataException("Signed-update download exceeded its fixed byte cap.");
                    }

                    total += read;
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (exactBytes.HasValue && total != exactBytes.Value)
                {
                    throw new InvalidDataException("Signed-update download byte count does not match the signed manifest.");
                }

                return new StreamCopyResult(total, Convert.ToHexString(hash.GetHashAndReset()));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            return statusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;
        }

        private static bool FixedHashEquals(string left, string right)
        {
            if (left.Length != 64 || right.Length != 64) return false;
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(left),
                    Convert.FromHexString(right));
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private readonly struct StreamCopyResult
        {
            public StreamCopyResult(long bytesWritten, string sha256)
            {
                BytesWritten = bytesWritten;
                Sha256 = sha256;
            }

            public long BytesWritten { get; }
            public string Sha256 { get; }
        }
    }
}
