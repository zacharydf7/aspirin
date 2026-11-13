namespace Handoff.Aspire.Hosting.Tests;

public class PackageChecksumsTests
{
    [Fact]
    public async Task WriteAsync_ShouldListEveryOtherFileInOrder_WhenCalled()
    {
        var root = Directory.CreateTempSubdirectory("handoff-checksums-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "resources", "worker"));
            await File.WriteAllTextAsync(Path.Combine(root, "package.json"), "abc", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "resources", "worker", "app.dll"), "", TestContext.Current.CancellationToken);

            // Written twice, so the second run sees the checksum file from the first and must leave it out.
            await PackageChecksums.WriteAsync(root, TestContext.Current.CancellationToken);
            await PackageChecksums.WriteAsync(root, TestContext.Current.CancellationToken);

            var lines = await File.ReadAllLinesAsync(Path.Combine(root, PackageChecksums.FileName), TestContext.Current.CancellationToken);
            Assert.Equal(
                [
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  package.json",
                    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  resources/worker/app.dll",
                ],
                lines);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
