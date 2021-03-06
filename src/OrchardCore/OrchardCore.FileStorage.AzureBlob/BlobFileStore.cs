using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.StaticFiles;
using OrchardCore.Modules;

namespace OrchardCore.FileStorage.AzureBlob
{
    /// <summary>
    /// Provides an <see cref="IFileStore"/> implementation that targets an underlying Azure Blob Storage account.
    /// </summary>
    /// <remarks>
    /// Azure Blob Storage has very different semantics for directories compared to a local file system, and
    /// some special consideration is required for make this provider conform to the semantics of the
    /// <see cref="IFileStore"/> interface and behave in an expected way.
    ///
    /// Directories have no physical manifestation in blob storage; we can obtain a reference to them, but
    /// that reference can be created regardless of whether the directory exists, and it can only be used
    /// as a scoping container to operate on blobs within that directory namespace.
    ///
    /// As a consequence, this provider generally behaves as if any given directory always exists. To
    /// simulate "creating" a directory (which cannot technically be done in blob storage) this provider creates
    /// a marker file inside the directory, which makes the directory "exist" and appear when listing contents
    /// subsequently. This marker file is ignored (excluded) when listing directory contents.
    ///
    /// Note that the Blob Container is not created automatically, and existence of the Container is not verified.
    ///
    /// Create the Blob Container before enabling a Blob File Store.
    ///
    /// Azure Blog Storage will create the BasePath inside the container during the upload of the first file.
    /// </remarks>
    public class BlobFileStore : IFileStore
    {
        private const string _directoryMarkerFileName = "OrchardCore.Media.txt";

        private readonly BlobStorageOptions _options;
        private readonly IClock _clock;
        private readonly BlobContainerClient _blobContainer;
        private readonly IContentTypeProvider _contentTypeProvider;
        private readonly string _basePrefix = null;

        public BlobFileStore(BlobStorageOptions options, IClock clock, IContentTypeProvider contentTypeProvider)
        {
            _options = options;
            _clock = clock;
            _contentTypeProvider = contentTypeProvider;

            _blobContainer = new BlobContainerClient(_options.ConnectionString, _options.ContainerName);

            if (!String.IsNullOrEmpty(_options.BasePath))
            {
                _basePrefix = this.NormalizePrefix(_options.BasePath);
            }
        }

        public async Task<IFileStoreEntry> GetFileInfoAsync(string path)
        {
            var blob = GetBlobReference(path);

            if (!await blob.ExistsAsync())
            {
                return null;
            }

            var properties = await blob.GetPropertiesAsync();

            return new BlobFile(path, properties.Value.ContentLength, properties.Value.LastModified);
        }

        public async Task<IFileStoreEntry> GetDirectoryInfoAsync(string path)
        {
            if (path == string.Empty)
            {
                return new BlobDirectory(path, _clock.UtcNow);
            }

            var blobDirectory = await GetBlobDirectoryReference(path);

            if (blobDirectory != null)
            {
                return new BlobDirectory(path, _clock.UtcNow);
            }

            return null;
        }

        public async Task<IEnumerable<IFileStoreEntry>> GetDirectoryContentAsync(string path = null, bool includeSubDirectories = false)
        {
            var results = new List<IFileStoreEntry>();

            var prefix = this.Combine(_basePrefix, path);
            prefix = this.NormalizePrefix(prefix);

            var page = _blobContainer.GetBlobsByHierarchyAsync(BlobTraits.Metadata, BlobStates.None, "/", prefix);
            await foreach (var blob in page)
            {
                if (blob.IsPrefix)
                {
                    var folderPath = blob.Prefix;
                    if (!String.IsNullOrEmpty(_basePrefix))
                    {
                        folderPath = folderPath.Substring(_basePrefix.Length - 1);
                    }

                    folderPath = folderPath.TrimEnd('/');
                    results.Add(new BlobDirectory(folderPath, _clock.UtcNow));
                }
                else
                {
                    var itemName = Path.GetFileName(WebUtility.UrlDecode(blob.Blob.Name));
                    // Ignore directory marker files.
                    if (includeSubDirectories || itemName != _directoryMarkerFileName)
                    {
                        var itemPath = this.Combine(path, itemName);
                        results.Add(new BlobFile(itemPath, blob.Blob.Properties.ContentLength, blob.Blob.Properties.LastModified));
                    }
                }
            }

            return results
                    .OrderByDescending(x => x.IsDirectory)
                    .ToArray();
        }

        public async Task<bool> TryCreateDirectoryAsync(string path)
        {
            // Since directories are only created implicitly when creating blobs, we
            // simply pretend like we created the directory, unless there is already
            // a blob with the same path.

            var blob = GetBlobReference(path);

            if (await blob.ExistsAsync())
            {
                throw new FileStoreException($"Cannot create directory because the path '{path}' already exists and is a file.");
            }

            await CreateDirectoryAsync(path);

            return true;
        }

        public async Task<bool> TryDeleteFileAsync(string path)
        {
            var blob = GetBlobReference(path);

            return await blob.DeleteIfExistsAsync();
        }

        public async Task<bool> TryDeleteDirectoryAsync(string path)
        {
            if (String.IsNullOrEmpty(path))
            {
                throw new FileStoreException("Cannot delete the root directory.");
            }

            var blobsWereDeleted = false;
            var prefix = this.Combine(_basePrefix, path);
            prefix = this.NormalizePrefix(prefix);

            var page = _blobContainer.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix);
            await foreach (var blob in page)
            {
                var blobReference = _blobContainer.GetBlobClient(blob.Name);
                await blobReference.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots);
                blobsWereDeleted = true;
            }

            return blobsWereDeleted;
        }

        public async Task MoveFileAsync(string oldPath, string newPath)
        {
            await CopyFileAsync(oldPath, newPath);
            await TryDeleteFileAsync(oldPath);
        }

        public async Task CopyFileAsync(string srcPath, string dstPath)
        {
            if (srcPath == dstPath)
            {
                throw new ArgumentException($"The values for {nameof(srcPath)} and {nameof(dstPath)} must not be the same.");
            }

            var oldBlob = GetBlobReference(srcPath);
            var newBlob = GetBlobReference(dstPath);

            if (!await oldBlob.ExistsAsync())
            {
                throw new FileStoreException($"Cannot copy file '{srcPath}' because it does not exist.");
            }

            if (await newBlob.ExistsAsync())
            {
                throw new FileStoreException($"Cannot copy file '{srcPath}' because a file already exists in the new path '{dstPath}'.");
            }

            await newBlob.StartCopyFromUriAsync(oldBlob.Uri);

            await Task.Delay(250);
            var properties = await newBlob.GetPropertiesAsync();

            while (properties.Value.CopyStatus == CopyStatus.Pending)
            {
                await Task.Delay(250);
                // Need to fetch properties or CopyStatus will never update.
                properties = await newBlob.GetPropertiesAsync();
            }

            if (properties.Value.CopyStatus != CopyStatus.Success)
            {
                throw new FileStoreException($"Error while copying file '{srcPath}'; copy operation failed with status {properties.Value.CopyStatus} and description {properties.Value.CopyStatusDescription}.");
            }
        }

        public async Task<Stream> GetFileStreamAsync(string path)
        {
            var blob = GetBlobReference(path);

            if (!await blob.ExistsAsync())
            {
                throw new FileStoreException($"Cannot get file stream because the file '{path}' does not exist.");
            }

            return (await blob.DownloadAsync()).Value.Content;
        }

        // Reduces the need to call blob.FetchAttributes, and blob.ExistsAsync,
        // as Azure Storage Library will perform these actions on OpenReadAsync(). 
        public Task<Stream> GetFileStreamAsync(IFileStoreEntry fileStoreEntry)
        {
            return GetFileStreamAsync(fileStoreEntry.Path);
        }

        public async Task<string> CreateFileFromStreamAsync(string path, Stream inputStream, bool overwrite = false)
        {
            var blob = GetBlobReference(path);

            if (!overwrite && await blob.ExistsAsync())
            {
                throw new FileStoreException($"Cannot create file '{path}' because it already exists.");
            }

            _contentTypeProvider.TryGetContentType(path, out var contentType);

            var headers = new BlobHttpHeaders
            {
                ContentType = contentType ?? "application/octet-stream"
            };

            await blob.UploadAsync(inputStream, headers);

            return path;
        }

        private BlobClient GetBlobReference(string path)
        {
            var blobPath = this.Combine(_options.BasePath, path);
            var blob = _blobContainer.GetBlobClient(blobPath);

            return blob;
        }

        private async Task<BlobHierarchyItem> GetBlobDirectoryReference(string path)
        {
            var prefix = this.Combine(_basePrefix, path);
            prefix = this.NormalizePrefix(prefix);

            // Directory exists if path contains any files.
            var page = _blobContainer.GetBlobsByHierarchyAsync(BlobTraits.Metadata, BlobStates.None, "/", prefix);

            var enumerator = page.GetAsyncEnumerator();

            var result = await enumerator.MoveNextAsync();
            if (result)
            {
                return enumerator.Current;
            }

            return null;
        }

        private async Task CreateDirectoryAsync(string path)
        {
            var placeholderBlob = GetBlobReference(this.Combine(path, _directoryMarkerFileName));

            // Create a directory marker file to make this directory appear when listing directories.
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("This is a directory marker file created by Orchard Core. It is safe to delete it.")))
            {
                await placeholderBlob.UploadAsync(stream);
            }
        }
    }

    internal static class BlogFileStoreExtensions
    {
        /// <summary>
        /// Blob prefix requires a trailing slash.
        /// </summary>
        internal static string NormalizePrefix(this BlobFileStore blobFileStore, string prefix)
        {
            return prefix.Trim('/') + '/';
        }
    }
}
