using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ExpectedObjects;
using ExpectedObjects.Comparisons;
using LightBlue.Standalone;
using Xunit;

namespace LightBlue.Tests.Standalone.BlobStorage
{
    public class StandaloneAzureBlockBlobCopyByUriTests : StandaloneAzureTestsBase
    {
        public StandaloneAzureBlockBlobCopyByUriTests()
            : base(DirectoryType.Container)
        { }

        public static IEnumerable<object[]> CopyBlobNames
        {
            get
            {
                yield return new object[] { "source", "destination" };
                yield return new object[] { "source", @"destination\with\path" };
                yield return new object[] { "source", "destination/with/path/alternate" };
                yield return new object[] { @"source\with\path", "destination" };
                yield return new object[] { "source/with/path/alternate", "destination" };
                yield return new object[] { @"source\with\path", @"destination\with\path" };
                yield return new object[] { "source/with/path/alternate", @"destination\with\path" };
                yield return new object[] { "source/with/path/alternate", @"destination/with/path/alternate" };
            }
        }

        [Theory]
        [MemberData(nameof(CopyBlobNames))]
        public void CanCopyBlob(string source, string destination)
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, source);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, destination);
            destinationBlob.StartCopyFromBlob(sourceBlob.Uri);

            Assert.Equal("File content", File.ReadAllText(destinationBlob.Uri.LocalPath));
        }

        [Theory]
        [MemberData(nameof(CopyBlobNames))]
        public void CanOverwriteBlob(string source, string destination)
        {
            var sourceBuffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, source);
            sourceBlob.UploadFromByteArrayAsync(sourceBuffer, 0, sourceBuffer.Length).Wait();

            var originalContentBuffer = Encoding.UTF8.GetBytes("Original content");
            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, destination);
            destinationBlob.UploadFromByteArrayAsync(originalContentBuffer, 0, originalContentBuffer.Length).Wait();
            destinationBlob.StartCopyFromBlob(sourceBlob.Uri);

            Assert.Equal("File content", File.ReadAllText(destinationBlob.Uri.LocalPath));
        }

        [Theory]
        [MemberData(nameof(CopyBlobNames))]
        public void WillNotCopyMetadataWhereItDoesNotExist(string source, string destination)
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, source);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, destination);
            destinationBlob.StartCopyFromBlob(sourceBlob.Uri);

            Assert.False(File.Exists(Path.Combine(BasePath, ".meta", destination)));
        }

        [Theory]
        [MemberData(nameof(CopyBlobNames))]
        public async Task WillCopyMetadataFromSourceWherePresent(string source, string destination)
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, source);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();
            sourceBlob.Metadata["thing"] = "something";
            sourceBlob.SetMetadata();
            sourceBlob.Properties.ContentType = "whatever";
            await sourceBlob.SetPropertiesAsync();

            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, destination);
            destinationBlob.StartCopyFromBlob(sourceBlob.Uri);
            destinationBlob.FetchAttributes();

            new
            {
                Metadata = new Dictionary<string, string>
                {
                    {"thing", "something"}
                },
                Properties = new
                {
                    ContentType = "whatever",
                    Length = (long)12
                }
            }.ToExpectedObject().ShouldMatch(destinationBlob);
        }

        [Theory]
        [MemberData(nameof(CopyBlobNames))]
        public void WillRemoveExistingMetadataWhereSourceDoesNotHaveMetadata(string source, string destination)
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, source);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            var originalContentBuffer = Encoding.UTF8.GetBytes("Original content");
            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, destination);
            destinationBlob.UploadFromByteArrayAsync(originalContentBuffer, 0, originalContentBuffer.Length).Wait();
            destinationBlob.Metadata["thing"] = "other thing";
            destinationBlob.SetMetadata();
            destinationBlob.StartCopyFromBlob(sourceBlob.Uri);

            Assert.False(File.Exists(Path.Combine(BasePath, ".meta", destination)));
        }

        [Theory]
        [MemberData(nameof(BlobNames))]
        public void CopyStateIsNullBeforeCopy(string blobName)
        {
            var blob = new StandaloneAzureBlockBlob(BasePath, blobName);

            Assert.Null(blob.CopyState);
        }

        [Theory]
        [MemberData(nameof(CopyBlobNames))]
        public void CopyStateIsSuccessAfterCopy(string source, string destination)
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, source);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, destination);
            destinationBlob.StartCopyFromBlob(sourceBlob.Uri);

            Assert.Equal(LightBlueBlobCopyStatus.Success, destinationBlob.CopyState.Status);
        }

        [Theory]
        [MemberData(nameof(CopyBlobNames))]
        public void CopyStateIsFailedIfBlobLocked(string source, string destination)
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, source);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            using (new FileStream(sourceBlob.Uri.LocalPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                var destinationBlob = new StandaloneAzureBlockBlob(BasePath, destination);
                destinationBlob.StartCopyFromBlob(sourceBlob.Uri);

                new
                {
                    Status = LightBlueBlobCopyStatus.Failed,
                    StatusDescription = new NotNullComparison()
                }.ToExpectedObject().ShouldMatch(destinationBlob.CopyState);
            }
        }
    }
}