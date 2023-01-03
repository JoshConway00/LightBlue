﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues.Models;
using Azure.Storage.Sas;
using LightBlue.Infrastructure;


namespace LightBlue.Standalone
{
    public class StandaloneAzureQueue : IAzureQueue
    {
        private static readonly Action<QueueMessage, string> _idAssigner = CloudQueueMessageAccessorFactory.BuildIdAssigner();
        private static readonly Func<string, QueueMessage> _queueMessageFactory = CloudQueueMessageAccessorFactory.BuildFactory();
        private static readonly ConcurrentDictionary<string, FileStream> _fileLocks = new ConcurrentDictionary<string, FileStream>();
        private static readonly int _processId = Process.GetCurrentProcess().Id;

        private static int _messageIncrement;

        private readonly Random _random = new Random();
        private readonly string _queueName;
        private readonly string _queueDirectory;

        public StandaloneAzureQueue(string queueStorageDirectory, string queueName)
        {
            _queueDirectory = Path.Combine(queueStorageDirectory, queueName);
            _queueName = queueName;
        }

        public StandaloneAzureQueue(string queueDirectory)
        {
            _queueDirectory = queueDirectory;
            _queueName = Path.GetFileName(_queueDirectory);
        }

        public StandaloneAzureQueue(Uri queueUri)
        {
            _queueDirectory = queueUri.GetLocalPathWithoutToken();
            _queueName = Path.GetFileName(_queueDirectory);
        }

        public Uri Uri
        {
            get { return new Uri(_queueDirectory); }
        }

        public string Name
        {
            get { return _queueName; }
        }

        public Task CreateIfNotExistsAsync()
        {
            Directory.CreateDirectory(_queueDirectory);

            return Task.FromResult(new object());
        }

        public Task DeleteAsync()
        {
            try
            {
                Directory.Delete(_queueDirectory, true);
                return Task.FromResult(new object());
            }
            catch (DirectoryNotFoundException ex)
            {
                throw new RequestFailedException(
                    $"The queue '{Name}' cannot be deleted as it does not exist",
                    ex);
            }
        }

        public Task DeleteIfExistsAsync()
        {
            try
            {
                Directory.Delete(_queueDirectory, true);
            }
            catch (DirectoryNotFoundException)
            {
                // it's already gone
            }
            return Task.FromResult(new object());
        }

        public async Task AddMessageAsync(string message)
        {
            var tries = 0;
            while (true)
            {
                var fileName = DetermineFileName();

                try
                {
                    using (var file = File.Open(fileName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = Encoding.UTF8.GetBytes(message);
                        await file.WriteAsync(buffer, 0, buffer.Length);
                        return;
                    }
                }
                catch (IOException)
                {
                    if (tries++ >= 3)
                    {
                        throw;
                    }
                }

                await Task.Delay(_random.Next(50, 1000));
            }
        }

        public async Task<QueueMessage> GetMessageAsync()
        {
            var files = Directory.GetFiles(_queueDirectory).OrderBy(f => f);

            foreach (var file in files)
            {
                FileStream fileStream = null;
                try
                {
                    var messageId = Path.GetFileName(file);
                    if (messageId == null)
                    {
                        throw new InvalidOperationException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Found a file path '{0}' with a null filename",
                                file));
                    }

                    fileStream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Delete);

                    var messageLength = (int) fileStream.Length;
                    var buffer = new byte[messageLength];
                    await fileStream.ReadAsync(buffer, 0, messageLength);

                    var cloudQueueMessage = _queueMessageFactory(Convert.ToBase64String(buffer)); // base64 to provide experience consistent with cloud
                    _idAssigner(cloudQueueMessage, messageId);

                    _fileLocks.AddOrUpdate(messageId, fileStream, (s, stream) => fileStream);

                    return cloudQueueMessage;
                }
                catch (IOException)
                {
                    fileStream?.Dispose();
                }
                catch (Exception)
                {
                    fileStream?.Dispose();
                    throw;
                }
            }

            return null;
        }

        public Task DeleteMessageAsync(string messageId, string popReceipt)
        {
            var filePath = Path.Combine(_queueDirectory, messageId);

            try
            {
                File.Delete(filePath);
            }
            finally
            {
                FileStream fileStream;
                if (_fileLocks.TryRemove(messageId, out fileStream))
                {
                    fileStream.Dispose();
                }
            }

            return Task.FromResult(new object());
        }

        public string GetSharedAccessSignature(QueueSasPermissions permissions, DateTimeOffset expiresOn)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "?sv={0:yyyy-MM-dd}&sr=c&sig=s&sp={1}",
                DateTime.Today,
                permissions.DeterminePermissionsString());
        }

        private string DetermineFileName()
        {
            return Path.Combine(
                _queueDirectory,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.{1}.{2}",
                    DateTimeOffset.UtcNow.Ticks,
                    Interlocked.Increment(ref _messageIncrement),
                    _processId));
        }
    }
}