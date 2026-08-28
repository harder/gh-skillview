using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class PathIdentityTests
{
    [Fact]
    public void Normalize_PreservesFilesystemRoot()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));

        Assert.NotNull(root);
        Assert.Equal(
            Path.GetFullPath(root).Replace('\\', '/'),
            PathIdentity.Normalize(root));
    }

    [Fact]
    public void NormalizeKey_InsensitiveVolumeFoldsMixedCase()
    {
        var lower = PathIdentity.NormalizeKey("/tmp/skills/demo", caseSensitive: false);
        var upper = PathIdentity.NormalizeKey("/TMP/SKILLS/DEMO", caseSensitive: false);

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void NormalizeKey_CaseSensitiveDirectoryPreservesDistinctNames()
    {
        var lower = PathIdentity.NormalizeKey("/tmp/skills/demo", caseSensitive: true);
        var upper = PathIdentity.NormalizeKey("/tmp/skills/Demo", caseSensitive: true);

        Assert.NotEqual(lower, upper);
    }

    [Fact]
    public void CurrentFilesystemProbe_MatchesObservedMixedCaseLookup()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-path-case-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "AlphaSkill");
        var alternate = Path.Combine(root, "alphaSkill");
        try
        {
            Directory.CreateDirectory(child);
            var observedCaseSensitive = !Directory.Exists(alternate);

            Assert.Equal(observedCaseSensitive, PathIdentity.IsCaseSensitive(child));
            if (observedCaseSensitive)
            {
                Assert.NotEqual(
                    PathIdentity.NormalizeKey(child),
                    PathIdentity.NormalizeKey(alternate));
            }
            else
            {
                Assert.Equal(
                    PathIdentity.NormalizeKey(child),
                    PathIdentity.NormalizeKey(alternate));
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsInside_UsesContainingRootFilesystemSemantics()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-path-root-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "Nested", "Skill");
        try
        {
            Directory.CreateDirectory(child);
            Assert.True(PathIdentity.IsInside(child, root));

            var mixedRoot = FlipLastAsciiCase(root);
            Assert.Equal(
                !PathIdentity.IsCaseSensitive(root),
                PathIdentity.IsInside(child, mixedRoot));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsInside_HandlesFilesystemRootWithoutDoubleSeparator()
    {
        var candidate = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetPathRoot(candidate);

        Assert.NotNull(root);
        Assert.True(PathIdentity.IsInside(candidate, root));
    }

    private static string FlipLastAsciiCase(string value)
    {
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (value[i] is >= 'a' and <= 'z')
            {
                return value[..i] + char.ToUpperInvariant(value[i]) + value[(i + 1)..];
            }
            if (value[i] is >= 'A' and <= 'Z')
            {
                return value[..i] + char.ToLowerInvariant(value[i]) + value[(i + 1)..];
            }
        }

        throw new InvalidOperationException("test path contains no ASCII letter");
    }
}
