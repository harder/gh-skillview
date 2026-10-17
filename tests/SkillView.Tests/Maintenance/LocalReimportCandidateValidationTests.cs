using System.Text;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class LocalReimportCandidateValidationTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-local-candidate-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temp.Delete(true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualArchivedSourceSpliceMatchesNativeCandidateAndAlteredBytesRefuse(bool altered)
    {
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.FullName, "source")).FullName;
        var installedPath = Directory.CreateDirectory(Path.Combine(temp.FullName, "installed")).FullName;
        var candidatePath = Directory.CreateDirectory(Path.Combine(temp.FullName, "candidate")).FullName;
        var raw = Encoding.UTF8.GetBytes("\uFEFF---\r\n# Preserve authored bytes\r\nname: demo\r\ndescription: Changed source\r\nmetadata:\r\n  custom: untouched\r\n---\r\nChanged source body 😀\r\n");
        File.WriteAllBytes(Path.Combine(sourcePath, "SKILL.md"), raw);
        File.WriteAllBytes(Path.Combine(sourcePath, "extra"), [0, 255, 3]);
        File.WriteAllText(Path.Combine(installedPath, "SKILL.md"), "---\nname: demo\ndescription: Old installed\n---\nI\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(installedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(candidatePath, File.GetUnixFileMode(installedPath));
            File.SetUnixFileMode(Path.Combine(sourcePath, "extra"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [sourcePath, installedPath, candidatePath], Token);
        using var writer = state.OpenFile("writer.lock", true, Token);
        using (var heldSource = NativeDirectoryMoveLease.Open(temp.FullName, sourcePath, Token))
            _ = CapturedPayloadArchive.Capture(state, "source-capture", heldSource, Token);
        using var archiveState = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "source-capture"), [], Token, allowCreate: false);
        var archive = CapturedPayloadArchive.Load(archiveState, null, Token).Archive;
        var skill = LocalTrackingWriter.Bind(GhLocalImportStager.ReadSkill(archive.OpenReader(archiveState), Token), sourcePath);
        Assert.NotEqual(raw, skill);
        var candidateSkill = skill.ToArray();
        if (altered) candidateSkill[^3] ^= 1; // Same length: the raw digest must detect the change.
        File.WriteAllBytes(Path.Combine(candidatePath, "SKILL.md"), candidateSkill);
        File.WriteAllBytes(Path.Combine(candidatePath, "extra"), File.ReadAllBytes(Path.Combine(sourcePath, "extra")));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(candidatePath, "extra"), File.GetUnixFileMode(Path.Combine(sourcePath, "extra")));
        using var installed = NativeDirectoryMoveLease.Open(temp.FullName, installedPath, Token);
        using var candidate = NativeDirectoryMoveLease.Open(temp.FullName, candidatePath, Token);
        var original = installed.Capture(Token);
        var capturedCandidate = candidate.Capture(Token);
        Assert.NotEqual(original.Fingerprint, capturedCandidate.Fingerprint);
        var validate = () => ReplacementPlanStore.ValidateLocalCandidateEntries(archive.Snapshot, skill,
            ReplacementEvidence.From(capturedCandidate), original.RootUnixMode);
        if (altered) Assert.Throws<IOException>(validate);
        else validate();
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(sourcePath, "SKILL.md")));
        // This exercises real archived/native byte projection only. It does not
        // bypass full metadata policy or claim a published reimport operation.
    }
}
