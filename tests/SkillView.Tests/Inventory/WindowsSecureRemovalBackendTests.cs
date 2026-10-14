using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class WindowsSecureRemovalBackendTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "skillview-win-native-" + Guid.NewGuid().ToString("N"));

    public WindowsSecureRemovalBackendTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(@"\\?\C:\skills\demo", @"C:\skills\demo")]
    [InlineData(@"\\?\UNC\server\share\demo", @"\\server\share\demo")]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\skills\demo", @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\skills\demo")]
    [InlineData(@"C:\skills\demo", @"C:\skills\demo")]
    public void NormalizeFinalPath_ConvertsExtendedDosPath(string path, string expected)
    {
        Assert.Equal(expected, WindowsSecureRemovalBackend.NormalizeFinalPath(path));
    }

    [Fact]
    public void LegacyDispositionInfo_UsesNativeBooleanWidth()
    {
        Assert.Equal(1, WindowsSecureRemovalBackend.LegacyDispositionInfoSize);
    }

    [Theory]
    [InlineData(88, 0, true)]
    [InlineData(100, 12, true)]
    [InlineData(100, 13, false)]
    [InlineData(87, 0, false)]
    [InlineData(100, -1, false)]
    public void HasCompleteDirectoryEntryHeader_RequiresEntireFixedHeader(
        int bufferLength,
        int offset,
        bool expected)
    {
        Assert.Equal(expected,
            WindowsSecureRemovalBackend.HasCompleteDirectoryEntryHeader(
                bufferLength,
                offset));
    }

    [Fact]
    public void TryDeleteWithFallback_AlreadyCanceled_DoesNotCallNativeBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attempts = 0;

        Assert.Throws<OperationCanceledException>(() =>
            WindowsSecureRemovalBackend.TryDeleteWithFallback(
                0,
                cancellation.Token,
                (_, _) =>
                {
                    attempts++;
                    return (true, 0);
                },
                out _));

        Assert.Equal(0, attempts);
    }

    [Fact]
    public void TryDeleteWithFallback_CanceledAfterExtendedFailure_SkipsLegacyBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = new List<bool>();

        Assert.Throws<OperationCanceledException>(() =>
            WindowsSecureRemovalBackend.TryDeleteWithFallback(
                0,
                cancellation.Token,
                (_, useExtendedDisposition) =>
                {
                    attempts.Add(useExtendedDisposition);
                    cancellation.Cancel();
                    return (false, 50);
                },
                out _));

        Assert.Equal([true], attempts);
    }

    [Fact]
    public void TryDeleteWithFallback_UnsupportedExtendedCall_UsesLegacyBoundary()
    {
        var attempts = new List<bool>();

        var deleted = WindowsSecureRemovalBackend.TryDeleteWithFallback(
            0,
            TestContext.Current.CancellationToken,
            (_, useExtendedDisposition) =>
            {
                attempts.Add(useExtendedDisposition);
                return useExtendedDisposition ? (false, 50) : (true, 0);
            },
            out var lastError);

        Assert.True(deleted);
        Assert.Equal(0, lastError);
        Assert.Equal([true, false], attempts);
    }

    [Fact]
    public void TryCaptureIdentity_UsesFinalPathFromOpenedHandle()
    {
        if (!OperatingSystem.IsWindows()) return;

        var realRoot = Path.Combine(_tempRoot, "real-root");
        var linkedRoot = Path.Combine(_tempRoot, "linked-root");
        var skill = Path.Combine(realRoot, "skill");
        Directory.CreateDirectory(skill);
        if (!TryCreateDirectoryLink(linkedRoot, realRoot)) return;

        var backend = new WindowsSecureRemovalBackend();
        var captured = backend.TryCaptureIdentity(
            Path.Combine(linkedRoot, "skill"),
            out var identity,
            out var error);

        Assert.True(captured, error);
        Assert.True(PathIdentity.Equals(skill, identity.CanonicalPath));
    }

    [Fact]
    public void TryCanonicalizePath_UsesFinalPathFromOpenedHandle()
    {
        if (!OperatingSystem.IsWindows()) return;

        var realRoot = Path.Combine(_tempRoot, "canonical-real");
        var linkedRoot = Path.Combine(_tempRoot, "canonical-link");
        Directory.CreateDirectory(realRoot);
        if (!TryCreateDirectoryLink(linkedRoot, realRoot)) return;

        var canonicalized = new WindowsSecureRemovalBackend().TryCanonicalizePath(
            linkedRoot,
            out var canonicalPath,
            out var error);

        Assert.True(canonicalized, error);
        Assert.True(PathIdentity.Equals(realRoot, canonicalPath));
    }

    [Fact]
    public void TryCaptureDirectoryValidationWithinRoot_RootLinkRetargeted_UsesHeldRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        var originalRoot = Path.Combine(_tempRoot, "root-original");
        var replacementRoot = Path.Combine(_tempRoot, "root-replacement");
        var linkedRoot = Path.Combine(_tempRoot, "root-link");
        var originalSkill = Path.Combine(originalRoot, "skill");
        var replacementSkill = Path.Combine(replacementRoot, "skill");
        Directory.CreateDirectory(Path.Combine(originalSkill, ".git"));
        Directory.CreateDirectory(replacementSkill);
        File.WriteAllText(Path.Combine(originalSkill, "SKILL.md"), "original");
        File.WriteAllText(Path.Combine(replacementSkill, "SKILL.md"), "replacement");
        if (!TryCreateDirectoryLink(linkedRoot, originalRoot)) return;

        var backend = new WindowsSecureRemovalBackend(
            rootIdentityCapturedForTests: () =>
            {
                Directory.Delete(linkedRoot);
                Directory.CreateSymbolicLink(linkedRoot, replacementRoot);
            });
        var captured = backend.TryCaptureDirectoryValidationWithinRoot(
            linkedRoot,
            Path.Combine(linkedRoot, "skill"),
            out var snapshot,
            out var error, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(captured, error);
        Assert.True(snapshot.Directory.HasGitDirectory);
        Assert.True(PathIdentity.Equals(
            originalRoot,
            snapshot.RootIdentity.CanonicalPath));
        Assert.True(PathIdentity.Equals(
            originalSkill,
            snapshot.Directory.Identity.CanonicalPath));
    }

    [Fact]
    public void TryCaptureLinkValidationWithinRoot_RootLinkRetargeted_UsesHeldRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        var originalRoot = Path.Combine(_tempRoot, "link-root-original");
        var replacementRoot = Path.Combine(_tempRoot, "link-root-replacement");
        var linkedRoot = Path.Combine(_tempRoot, "link-root-link");
        var originalLink = Path.Combine(originalRoot, "broken-link");
        var replacementLink = Path.Combine(replacementRoot, "broken-link");
        var replacementTarget = Path.Combine(replacementRoot, "valid-target");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(replacementTarget);
        if (!TryCreateDirectoryLink(originalLink, Path.Combine(originalRoot, "missing"))) return;
        if (!TryCreateDirectoryLink(replacementLink, replacementTarget)) return;
        if (!TryCreateDirectoryLink(linkedRoot, originalRoot)) return;

        var backend = new WindowsSecureRemovalBackend(
            rootIdentityCapturedForTests: () =>
            {
                Directory.Delete(linkedRoot);
                Directory.CreateSymbolicLink(linkedRoot, replacementRoot);
            });
        var captured = backend.TryCaptureLinkValidationWithinRoot(
            linkedRoot,
            Path.Combine(linkedRoot, "broken-link"),
            out var snapshot,
            out var error);

        Assert.True(captured, error);
        Assert.True(snapshot.Link.IsBroken);
        Assert.True(PathIdentity.Equals(
            originalRoot,
            snapshot.RootIdentity.CanonicalPath));
        Assert.True(PathIdentity.Equals(
            originalLink,
            snapshot.Link.Identity.CanonicalPath));
    }

    [Fact]
    public void TryCaptureDirectoryValidation_TargetAba_InspectsOpenedDirectoryOrFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = Path.Combine(_tempRoot, "policy-parent");
        var skill = Path.Combine(parent, "skill");
        var movedSkill = Path.Combine(parent, "skill-original");
        Directory.CreateDirectory(Path.Combine(skill, ".git"));
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "body");
        var backend = new WindowsSecureRemovalBackend(() =>
        {
            Directory.Move(skill, movedSkill);
            Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "clean");
        });

        var captured = backend.TryCaptureDirectoryValidation(
            skill,
            out var snapshot,
            out var error, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Combine(movedSkill, ".git")));
        Assert.False(Directory.Exists(Path.Combine(skill, ".git")));
        if (!captured)
        {
            Assert.Equal(
                "selected directory changed during policy inspection",
                error);
            return;
        }

        Assert.True(snapshot.HasSkillFile);
        Assert.True(snapshot.HasGitDirectory);
        Assert.True(PathIdentity.Equals(movedSkill, snapshot.Identity.CanonicalPath));
    }

    [Fact]
    public void TryCaptureLinkValidation_BrokenLinkReplacedWithValidLink_FailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var target = Path.Combine(_tempRoot, "valid-target");
        var link = Path.Combine(_tempRoot, "changing-broken-link");
        Directory.CreateDirectory(target);
        if (!TryCreateDirectoryLink(link, Path.Combine(_tempRoot, "missing"))) return;
        var backend = new WindowsSecureRemovalBackend(linkTargetObservedForTests: () =>
        {
            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, target);
        });

        var captured = backend.TryCaptureLinkValidation(link, out _, out var error);

        Assert.False(captured);
        Assert.NotNull(error);
        Assert.True(Directory.Exists(target));
        Assert.True(PathResolver.IsSymlink(link));
    }

    [Fact]
    public void RemoveTree_ChangedLinkEvenWithCurrentParentIdentity_RefusesBeforeMutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var skill = Path.Combine(_tempRoot, "skill");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "payload");
        var first = Directory.CreateDirectory(Path.Combine(_tempRoot, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_tempRoot, "second")).FullName;
        var link = Path.Combine(skill, "changing-link");
        Assert.True(TryCreateDirectoryLink(link, first), "Windows link support is required for this regression");
        var backend = new WindowsSecureRemovalBackend();
        Assert.True(backend.TryCaptureDirectoryValidation(skill, out var captured, out var error,
            TestContext.Current.CancellationToken), error);
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, second);
        // Deliberately refresh parent generation: the stale child snapshot must
        // independently refuse mutation even when the parent identity matches.
        Assert.True(backend.TryCaptureIdentity(skill, out var current, out error), error);
        var expected = current with { DirectoryFingerprint = captured.Identity.DirectoryFingerprint };
        var deletes = 0;
        var failures = new List<string>();
        backend.RemoveTree(skill, expected, false, 64, _ => { }, (_, _) => deletes++,
            (_, _) => deletes++, (_, message) => failures.Add(message),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, deletes);
        Assert.Contains(failures, message => message.Contains("identity changed", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(skill, "SKILL.md")));
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    public void CaptureDirectoryValidation_MutationBetweenSnapshots_Refuses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var backend = new WindowsSecureRemovalBackend(directoryFingerprintCapturedForTests: () =>
            File.WriteAllText(Path.Combine(_tempRoot, "new-entry"), "changed"));
        Assert.False(backend.TryCaptureDirectoryValidation(_tempRoot, out _, out var error,
            TestContext.Current.CancellationToken));
        Assert.Contains("changed during policy inspection", error);
    }

    [Fact]
    public void CaptureDirectoryValidation_EntryLimit_Refuses()
    {
        if (!OperatingSystem.IsWindows()) return;
        for (var index = 0; index <= WindowsSecureRemovalBackend.MaxFingerprintEntries; index++)
            File.WriteAllText(Path.Combine(_tempRoot, $"entry-{index}"), "");
        Assert.False(new WindowsSecureRemovalBackend().TryCaptureDirectoryValidation(
            _tempRoot, out _, out var error, TestContext.Current.CancellationToken));
        Assert.Contains("immediate-entry limit", error);
    }

    [Fact]
    public void CaptureDirectoryValidation_CanceledAfterFirstSnapshot_Throws()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource();
        var backend = new WindowsSecureRemovalBackend(
            directoryFingerprintCapturedForTests: cancellation.Cancel);
        Assert.Throws<OperationCanceledException>(() => backend.TryCaptureDirectoryValidation(
            _tempRoot, out _, out _, cancellation.Token));
    }

    [Fact]
    public void CaptureDirectoryValidation_UnchangedEntries_ProducesStableFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_tempRoot, "SKILL.md"), "payload");
        var backend = new WindowsSecureRemovalBackend();
        Assert.True(backend.TryCaptureDirectoryValidation(_tempRoot, out var first, out var error,
            TestContext.Current.CancellationToken), error);
        Assert.True(backend.TryCaptureDirectoryValidation(_tempRoot, out var second, out error,
            TestContext.Current.CancellationToken), error);
        Assert.NotNull(first.Identity.DirectoryFingerprint);
        Assert.Equal(first.Identity.DirectoryFingerprint, second.Identity.DirectoryFingerprint);
    }

    [Fact]
    public void CaptureDirectoryValidation_CanceledBetweenEntries_StopsAdmission()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_tempRoot, "first"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "second"), "");
        using var cancellation = new CancellationTokenSource();
        var captures = 0;
        var admissions = 0;
        var backend = new WindowsSecureRemovalBackend(fingerprintEntryCapturedForTests: () =>
        {
            captures++;
            cancellation.Cancel();
        }, fingerprintEnumerationAdmittedForTests: () => admissions++);
        Assert.Throws<OperationCanceledException>(() => backend.TryCaptureDirectoryValidation(
            _tempRoot, out _, out _, cancellation.Token));
        Assert.Equal(1, captures);
        Assert.Equal(1, admissions);
    }

    [Fact]
    public void RemoveTree_InPlaceRetargetWithUnchangedLinkIdentity_RefusesBeforeMutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var skill = Directory.CreateDirectory(Path.Combine(_tempRoot, "skill")).FullName;
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "payload");
        var first = Directory.CreateDirectory(Path.Combine(_tempRoot, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_tempRoot, "second")).FullName;
        var link = Path.Combine(skill, "link");
        var donor = Path.Combine(_tempRoot, "donor-link");
        Assert.True(TryCreateDirectoryLink(link, first), "Windows link support is required for this regression");
        Assert.True(TryCreateDirectoryLink(donor, second), "Windows link support is required for this regression");
        var backend = new WindowsSecureRemovalBackend();
        Assert.True(backend.TryCaptureDirectoryValidation(skill, out var captured, out var error,
            TestContext.Current.CancellationToken), error);
        Assert.True(backend.TryCaptureLinkIdentity(link, out var original, out error), error);
        using (var donorHandle = OpenReparseHandle(donor))
        using (var linkHandle = OpenReparseHandle(link))
        {
            var buffer = new byte[16 * 1024];
            Assert.True(ReparseNative.DeviceIoControl(donorHandle, 0x000900A8, null, 0,
                buffer, (uint)buffer.Length, out var returned, IntPtr.Zero),
                $"get reparse error {Marshal.GetLastPInvokeError()}");
            Assert.True(ReparseNative.DeviceIoControl(linkHandle, 0x000900A4, buffer, returned,
                null, 0, out _, IntPtr.Zero), $"set reparse error {Marshal.GetLastPInvokeError()}");
            // Neutralize timestamps as well as preserving the actual native ID.
            // Only the captured reparse bytes can then distinguish the target.
            var basic = new ReparseNative.BasicInfo { ChangeTime = original.LinkIdentity.WindowsChangeTime };
            Assert.True(ReparseNative.SetFileInformationByHandle(linkHandle, 0, ref basic,
                Marshal.SizeOf<ReparseNative.BasicInfo>()), $"set time error {Marshal.GetLastPInvokeError()}");
        }
        Assert.True(backend.TryCaptureLinkIdentity(link, out var retargeted, out error), error);
        Assert.Equal(original.LinkIdentity, retargeted.LinkIdentity);
        Assert.True(backend.TryCaptureIdentity(skill, out var parent, out error), error);
        var deletes = 0;
        var failures = new List<string>();
        backend.RemoveTree(skill, parent with { DirectoryFingerprint = captured.Identity.DirectoryFingerprint },
            false, 64, _ => { }, (_, _) => deletes++, (_, _) => deletes++,
            (_, message) => failures.Add(message), TestContext.Current.CancellationToken);
        Assert.Equal(0, deletes);
        Assert.Contains(failures, message => message.Contains("identity changed", StringComparison.Ordinal));
        Assert.Equal(second, new DirectoryInfo(link).ResolveLinkTarget(false)?.FullName);
        Assert.True(File.Exists(Path.Combine(skill, "SKILL.md")));
    }

    private static SafeFileHandle OpenReparseHandle(string path)
    {
        var handle = ReparseNative.CreateFileW(path, 0xC0000000, 7, IntPtr.Zero, 3,
            0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new IOException($"open reparse handle failed: {error}");
    }

    private static class ReparseNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicInfo
        {
            internal long CreationTime;
            internal long LastAccessTime;
            internal long LastWriteTime;
            internal long ChangeTime;
            internal uint FileAttributes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access,
            uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
            byte[]? input, uint inputSize, [Out] byte[]? output, uint outputSize,
            out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle handle,
            int informationClass, ref BasicInfo information, int size);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
