using System.Globalization;

namespace SkillView.Diagnostics;

/// Tiny semver-ish parser. Accepts `MAJOR.MINOR.PATCH` with optional trailing
/// pre-release / build metadata that we ignore. `gh --version` emits
/// `gh version 2.91.0 (2026-03-…)` — the `2.91.0` is what we compare.
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public static bool TryParse(string? input, out SemVer value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        // strip any trailing `-prerelease` / `+build` / ` (date)` fragment
        var cutIdx = trimmed.AsSpan().IndexOfAny(new[] { '-', '+', ' ', '(' });
        if (cutIdx >= 0)
        {
            trimmed = trimmed[..cutIdx];
        }
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var parts = trimmed.Split('.');
        if (parts.Length < 2 || parts.Length > 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        var patch = 0;
        if (parts.Length == 3 &&
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        if (major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        value = new SemVer(major, minor, patch);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        return Patch.CompareTo(other.Patch);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
}
