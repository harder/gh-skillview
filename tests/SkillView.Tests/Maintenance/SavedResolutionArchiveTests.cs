using System.Security.Cryptography;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class SavedResolutionArchiveTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-saved-resolution-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temporary.Delete(true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedResolutionVerifiesFrozenBytesAndRefusesCorruptionAfterInputDisappears(bool corrupt)
    {
        var input = Path.Combine(temporary.FullName, "source.bin");
        byte[] content = [0, 255, 7]; File.WriteAllBytes(input, content);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temporary.FullName, "state"), [], Token);
        using var archive = state.CreateStagingDirectory("resolutions-00", Token);
        using var authority = PayloadMetadataAuthority.Open(archive, create: true, Token);
        PayloadNativeMetadata? metadata = null;
        RegularFileInspection.Capture(input, item =>
        {
            metadata = PayloadNativeMetadata.Capture(item, Token, authority);
            return new(Convert.ToHexString(SHA256.HashData(content)), content.Length);
        }, Token);
        var hash = Convert.ToHexString(SHA256.HashData(content));
        var entry = new ArchivedPayloadEntry(new("resource", "file", hash, hash, content.Length, metadata!.UnixMode, null),
            "entry-00000", content.Length, metadata);
        using (var blob = archive.OpenFile("entry-00000", false, Token)) { blob.Write(content); blob.Flush(true); }
        string manifest;
        using (var file = archive.OpenFile("manifest.json", false, Token))
        {
            manifest = BoundedReviewJson.Write(file, new ResolutionArchiveManifest(1, [entry]),
                ResolutionJsonContext.Default.ResolutionArchiveManifest, CapturedPayloadArchive.MaxManifestBytes, Token);
            file.Flush(true);
        }
        archive.SealStagingDirectory(448, Token);
        File.Delete(input);
        using var frozen = NativeStateDirectory.OpenOrCreate(archive.CanonicalPath, [], Token, allowCreate: false);
        var saved = CapturedResolutionArchive.Load(frozen, manifest, Token);
        Assert.Equal(metadata.Identity, Assert.Single(saved.Entries).Metadata!.Identity);
        Assert.Throws<IOException>(() => CapturedResolutionArchive.Load(frozen, new('f', 64), Token));
        if (corrupt) File.WriteAllBytes(Path.Combine(archive.CanonicalPath, "entry-00000"), [7, 0, 255]);
        using var bytes = new MemoryStream();
        if (corrupt) Assert.Throws<IOException>(() => saved.OpenReader(frozen).CopyEntry("resource", bytes, Token));
        else { saved.OpenReader(frozen).CopyEntry("resource", bytes, Token); Assert.Equal(content, bytes.ToArray()); }
    }
}
