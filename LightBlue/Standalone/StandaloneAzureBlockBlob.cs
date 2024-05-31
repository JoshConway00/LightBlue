﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Sas;
using LightBlue.Infrastructure;

namespace LightBlue.Standalone
{
    public class StandaloneAzureBlockBlob : IAzureBlockBlob
    {
        internal const int BufferSize = 4096;
        private const int MaxFileLockRetryAttempts = 5;
        private const string DefaultContentType = "application/octet-stream";

        private readonly string _blobName;
        private readonly string _blobPath;
        private readonly string _metadataPath;
        private readonly StandaloneAzureBlobProperties _properties;
        private Dictionary<string, string> _metadata;
        private readonly TimeSpan _waitTimeBetweenRetries = TimeSpan.FromSeconds(5);
        private string _blobDirectory;
        private string _metadataDirectory;

        public StandaloneAzureBlockBlob(string containerDirectory, string blobName)
        {
            StringValidation.NotNullOrWhitespace(containerDirectory, "containerDirectory");
            StringValidation.NotNullOrWhitespace(blobName, "blobName");

            _blobName = blobName;
            _blobPath = Path.Combine(containerDirectory, blobName);
            _metadataPath = Path.Combine(containerDirectory, ".meta", blobName);

            var blobDirectory = Path.GetDirectoryName(_blobPath);
            if (blobDirectory == null)
            {
                throw new InvalidOperationException("Could not determine the blob directory");
            }
            if (blobDirectory != containerDirectory)
            {
                if (!Directory.Exists(blobDirectory))
                {
                    _blobDirectory = blobDirectory;
                }
                var metadataDirectory = Path.GetDirectoryName(_metadataPath);
                if (metadataDirectory != null && !Directory.Exists(metadataDirectory))
                {
                    _metadataDirectory = metadataDirectory;
                }
            }

            _metadata = new Dictionary<string, string>();
            _properties = new StandaloneAzureBlobProperties { Length = -1, ContentType = null };
        }

        public Uri Uri
        {
            get { return new Uri(_blobPath); }
        }

        public string Name
        {
            get { return _blobName; }
        }

        public IAzureBlobProperties Properties
        {
            get { return _properties; }
        }

        public IAzureCopyState CopyState { get; private set; }

        public IDictionary<string, string> Metadata
        {
            get { return _metadata; }
        }

        public void Delete()
        {
            File.Delete(_blobPath);
            if (File.Exists(_metadataPath))
            {
                File.Delete(_metadataPath);
            }
        }

        public Task DeleteAsync()
        {
            Delete();
            return Task.FromResult(new object());
        }

        public bool Exists()
        {
            return File.Exists(_blobPath);
        }

        public Task<bool> ExistsAsync()
        {
            return Task.FromResult(File.Exists(_blobPath));
        }

        public void FetchAttributes()
        {
            var fileInfo = EnsureBlobFileExists();
            var metadataStore = LoadMetadataStore();
            _properties.ContentType = metadataStore.ContentType;
            _properties.Length = fileInfo.Length;
            _metadata = metadataStore.Metadata;
        }

        public async Task FetchAttributesAsync()
        {
            var fileInfo = EnsureBlobFileExists();
            var metadataStore = await LoadMetadataStoreAsync();
            _properties.ContentType = metadataStore.ContentType;
            _properties.Length = fileInfo.Length;
            _metadata = metadataStore.Metadata;
        }

        public Stream OpenRead()
        {
            return new FileStream(_blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, true);
        }

        public void SetMetadata()
        {
            EnsureBlobFileExists();
            EnsureMetadataDirectoryExists();

            FileLockExtensions.WaitAndRetryOnFileLock(
                SetMetadataImplementation,
                _waitTimeBetweenRetries,
                MaxFileLockRetryAttempts,
                WhenSetMetadataFileHasSharingViolation);
        }

        public Task SetMetadataAsync()
        {
            SetMetadata();
            return Task.FromResult(new object());
        }

        private void SetMetadataImplementation()
        {
            using (var fileStream = new FileStream(_metadataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, BufferSize, true))
            {
                var metadataStore = fileStream.Length == 0
                    ? CreateStandaloneMetadataStore()
                    : StandaloneMetadataStore.ReadFromStream(fileStream);

                foreach (var key in _metadata.Keys)
                {
                    metadataStore.Metadata[key] = _metadata[key];
                }

                fileStream.SetLength(0);
                metadataStore.WriteToStreamAndClose(fileStream);
            }
        }

        private void WhenSetMetadataFileHasSharingViolation(int retriesRemaining)
        {
            if (retriesRemaining <= 0)
            {
                throw new RequestFailedException($"Tried {MaxFileLockRetryAttempts} times to write to locked metadata file '{_metadataPath}'");
            }
        }

        private void SetPropertiesImplementation()
        {
            using (var fileStream = new FileStream(_metadataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, BufferSize, true))
            {
                var metadataStore = fileStream.Length == 0
                    ? CreateStandaloneMetadataStore()
                    : StandaloneMetadataStore.ReadFromStream(fileStream);

                metadataStore.ContentType = _properties.ContentType;

                fileStream.SetLength(0);
                metadataStore.WriteToStreamAndClose(fileStream);
            }
        }

        public Task SetPropertiesAsync()
        {
            EnsureBlobFileExists();
            EnsureMetadataDirectoryExists();

            FileLockExtensions.WaitAndRetryOnFileLock(
                SetPropertiesImplementation,
                _waitTimeBetweenRetries,
                MaxFileLockRetryAttempts,
                WhenSetMetadataFileHasSharingViolation);
            return Task.FromResult(new object());
        }

        public string GetSharedAccessReadSignature(DateTimeOffset expiresOn)
        {
            var permissions = BlobSasPermissions.Read;
            return string.Format(
                CultureInfo.InvariantCulture,
                "?sv={0:yyyy-MM-dd}&sr=b&sig=s&sp={1}",
                DateTime.Today,
                permissions.DeterminePermissionsString());
        }

        public string GetSharedAccessWriteSignature(DateTimeOffset expiresOn)
        {
            var permissions = BlobSasPermissions.Write;
            return string.Format(
                CultureInfo.InvariantCulture,
                "?sv={0:yyyy-MM-dd}&sr=b&sig=s&sp={1}",
                DateTime.Today,
                permissions.DeterminePermissionsString());
        }

        public string GetSharedAccessReadWriteSignature(DateTimeOffset expiresOn)
        {
            var permissions = BlobSasPermissions.Read | BlobSasPermissions.Write;
            return string.Format(
                CultureInfo.InvariantCulture,
                "?sv={0:yyyy-MM-dd}&sr=b&sig=s&sp={1}",
                DateTime.Today,
                permissions.DeterminePermissionsString());
        }

        public void DownloadToStream(Stream target, CancellationToken cancellationToken = default)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            try
            {
                using (var fileStream = new FileStream(_blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, true))
                {
                    var buffer = new byte[BufferSize];
                    int bytesRead;
                    do
                    {
                        bytesRead = fileStream.Read(buffer, 0, buffer.Length);

                        target.Write(buffer, 0, bytesRead);
                    } while (bytesRead == BufferSize);
                }

                //Replicate Hosted Beahviour or Attribute Fetch on Download
                FetchAttributes();
            }
            catch (FileNotFoundException ex)
            {
                throw new RequestFailedException(
                    $"The blob file was not found. Expected location '{_blobPath}'.",
                    ex);
            }
        }

        public Task DownloadToStreamAsync(Stream target, CancellationToken cancellationToken = default)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            return DownloadToStreamInternalAsync(target);
        }

        private async Task DownloadToStreamInternalAsync(Stream target)
        {
            try
            {
                using (var fileStream = new FileStream(_blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, true))
                {
                    var buffer = new byte[BufferSize];
                    int bytesRead;
                    do
                    {
                        bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);

                        await target.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                    } while (bytesRead == BufferSize);
                }

                //Replicate Hosted Beahviour or Attribute Fetch on Download
                await FetchAttributesAsync();
            }
            catch (FileNotFoundException ex)
            {
                throw new RequestFailedException(
                    $"The blob file was not found. Expected location '{_blobPath}'.",
                    ex);
            }
        }

        public Task UploadFromStreamAsync(Stream source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            EnsureBlobDirectoryExists();
            return UploadFromStreamInternalAsync(source);
        }

        private async Task UploadFromStreamInternalAsync(Stream source)
        {
            using (var fileStream = new FileStream(_blobPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                var buffer = new byte[BufferSize];
                int bytesRead;
                do
                {
                    bytesRead = await source.ReadAsync(buffer, 0, buffer.Length);

                    await fileStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                } while (bytesRead == BufferSize);
            }
        }

        public Task UploadFromFileAsync(string path)
        {
            EnsureBlobDirectoryExists();
            File.Copy(path, _blobPath, true);

            return Task.FromResult(new object());
        }

        public Task UploadFromByteArrayAsync(byte[] buffer)
        {
            return UploadFromByteArrayAsync(buffer, 0, buffer.Length);
        }

        public Task UploadFromByteArrayAsync(byte[] buffer, int index, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (index < 0 || (buffer.Length > 0 && index >= buffer.Length))
                throw new ArgumentOutOfRangeException(nameof(index));

            if (count < 0 || index + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            EnsureBlobDirectoryExists();
            return UploadFromByteArrayInternalAsync(buffer, index, count);
        }

        private async Task UploadFromByteArrayInternalAsync(byte[] buffer, int index, int count)
        {
            using (var fileStream = new FileStream(_blobPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                await fileStream.WriteAsync(buffer, index, count).ConfigureAwait(false);
            }
        }

        public string StartCopyFromBlob(IAzureBlockBlob source)
        {
            if (!(source is StandaloneAzureBlockBlob standaloneAzureBlockBlob))
            {
                throw new ArgumentException("Can only copy between blobs in the same hosting environment");
            }

            try
            {
                EnsureBlobDirectoryExists();
                EnsureMetadataDirectoryExists();

                RetryFileOperation(() => File.Copy(standaloneAzureBlockBlob._blobPath, _blobPath, true));
                if (File.Exists(standaloneAzureBlockBlob._metadataPath))
                {
                    RetryFileOperation(() => File.Copy(standaloneAzureBlockBlob._metadataPath, _metadataPath, true));
                }
                else
                {
                    RetryFileOperation(() => File.Delete(_metadataPath));
                }

                CopyState = new StandaloneAzureCopyState(LightBlueBlobCopyStatus.Success, null);
            }
            catch (IOException ex)
            {
                CopyState = new StandaloneAzureCopyState(LightBlueBlobCopyStatus.Failed, ex.ToTraceMessage());
            }
            return Guid.NewGuid().ToString();
        }

        public string StartCopyFromBlob(Uri source)
        {
            var parts = StandaloneEnvironment.SeparateBlobUri(source);
            var sourceBlob = new StandaloneAzureBlockBlob(Path.Combine(StandaloneEnvironment.LightBlueDataDirectory, parts.ContainerPath), parts.BlobPath);
            return StartCopyFromBlob(sourceBlob);
        }

        private FileInfo EnsureBlobFileExists()
        {
            var fileInfo = new FileInfo(_blobPath);
            if (!fileInfo.Exists)
            {
                throw new RequestFailedException("The specified blob does not exist");
            }
            return fileInfo;
        }

        private static void RetryFileOperation(Action fileOperation)
        {
            var retryCount = 0;
            while (true)
            {
                try
                {
                    fileOperation();
                    break;
                }
                catch (IOException)
                {
                    if (retryCount++ >= 4)
                    {
                        throw;
                    }

                    Thread.Sleep(retryCount * 100);
                }
            }
        }

        private StandaloneMetadataStore LoadMetadataStore()
        {
            if (!File.Exists(_metadataPath))
            {
                return CreateStandaloneMetadataStore();
            }

            using (var fileStream = new FileStream(_metadataPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, BufferSize, true))
            {
                return StandaloneMetadataStore.ReadFromStream(fileStream);
            }
        }

        private async Task<StandaloneMetadataStore> LoadMetadataStoreAsync()
        {
            if (!File.Exists(_metadataPath))
            {
                return CreateStandaloneMetadataStore();
            }

            using (var fileStream = new FileStream(_metadataPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, BufferSize, true))
            {
                return await StandaloneMetadataStore.ReadFromStreamAsync(fileStream);
            }
        }

        private StandaloneMetadataStore CreateStandaloneMetadataStore()
        {
            return new StandaloneMetadataStore
            {
                ContentType = File.Exists(_blobPath) ? DefaultContentType : null,
                Metadata = new Dictionary<string, string>()
            };
        }

        private void EnsureBlobDirectoryExists()
        {
            if (_blobDirectory == null)
            {
                return;
            }

            Directory.CreateDirectory(_blobDirectory);
            _blobDirectory = null;
        }

        private void EnsureMetadataDirectoryExists()
        {
            if (_metadataDirectory == null)
            {
                return;
            }

            Directory.CreateDirectory(_metadataDirectory);
            _metadataDirectory = null;
        }
    }
}