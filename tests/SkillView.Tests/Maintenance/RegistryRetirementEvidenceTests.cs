using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class RegistryRetirementEvidenceTests
{
    // Synthetic evidence tests only this format/purpose predicate. Positive
    // mutation authority is proved separately from genuine registry fixtures.
    private static (ReplacementPlan Plan, RetirementDirectory Directory) Evidence()
    {
        var original = new ReplacementEvidence(PayloadSnapshot.Digest([], false), new('a', 64), 448,
            new(1, 2, 3, "/live/demo", true, false, 10, 20, 30, 40), [], []);
        var current = original with
        {
            Objects = new('b', 64),
            Identity = original.Identity with { CanonicalPath = "/private/original", ChangeTimeSeconds = 11, WindowsChangeTime = 41 }
        };
        var plan = new ReplacementPlan(1, new('c', 32), ReplacementPlanStore.RegistryRestorePurpose, DateTimeOffset.UnixEpoch,
            "/live/demo", "/live", default, default,
            new("copy", "demo", Scope.User, [], SkillFrontMatter.Empty, []), new('d', 64), original, original, [], new('e', 64),
            RegistryRestore: new(ValidityState.MissingSkillMd, SkillFrontMatter.Empty, null, false, []));
        return (plan, new("original", current));
    }

    [Fact]
    public void RenamedEmptyOriginalKeepsStableIdentityWithoutReusingItsOldGenerationHash()
    {
        var (plan, directory) = Evidence();
        Assert.NotEqual(plan.Original.Objects, directory.Evidence.Objects);
        Assert.NotEqual(plan.Original.Identity, directory.Evidence.Identity);
        ReplacementPlanStore.ValidateRetirementDirectoryEvidence(plan, directory, "installed");
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("policy")]
    [InlineData("ready")]
    [InlineData("restored")]
    [InlineData("candidate")]
    [InlineData("displaced")]
    [InlineData("review-installed-candidate")]
    [InlineData("review-source-candidate")]
    [InlineData("recorded-base")]
    [InlineData("unknown")]
    [InlineData("original-entries")]
    [InlineData("original-children")]
    [InlineData("current-entries")]
    [InlineData("current-children")]
    [InlineData("payload")]
    [InlineData("mode")]
    [InlineData("volume")]
    [InlineData("id-low")]
    [InlineData("id-high")]
    [InlineData("creation-time")]
    [InlineData("nondirectory")]
    [InlineData("reparse")]
    [InlineData("malformed-objects")]
    public void EmptyEvidenceRefusesOutsideTheExactImmutableOriginalRule(string change)
    {
        var (plan, directory) = Evidence();
        var topology = change is "ready" or "restored" ? change : "installed";
        var entry = new PayloadEntry("extra", "file", new('f', 64), new('f', 64), 1, 384, null);
        var child = new PayloadObjectEvidence("extra", directory.Evidence.Identity with { IsDirectory = false }, 384);
        if (change == "purpose") plan = plan with { Purpose = "restore-recorded" };
        else if (change == "policy") plan = plan with { RegistryRestore = null };
        else if (change == "original-entries") plan = plan with { Original = plan.Original with { Entries = [entry] } };
        else if (change == "original-children") plan = plan with { Original = plan.Original with { Children = [child] } };
        else if (change == "current-entries") directory = directory with { Evidence = directory.Evidence with { Entries = [entry] } };
        else if (change == "current-children") directory = directory with { Evidence = directory.Evidence with { Children = [child] } };
        else if (change == "payload") directory = directory with { Evidence = directory.Evidence with { Payload = new('f', 64) } };
        else if (change == "mode") directory = directory with { Evidence = directory.Evidence with { Mode = 493 } };
        else if (change == "malformed-objects") directory = directory with { Evidence = directory.Evidence with { Objects = "malformed" } };
        else if (change is "volume" or "id-low" or "id-high" or "creation-time" or "nondirectory" or "reparse")
        {
            var identity = directory.Evidence.Identity;
            identity = change switch
            {
                "volume" => identity with { Volume = 9 },
                "id-low" => identity with { FileIdLow = 9 },
                "id-high" => identity with { FileIdHigh = 9 },
                "creation-time" => identity with { WindowsCreationTime = 9 },
                "nondirectory" => identity with { IsDirectory = false },
                _ => identity with { IsReparsePoint = true }
            };
            directory = directory with { Evidence = directory.Evidence with { Identity = identity } };
        }
        else if (change is not ("ready" or "restored")) directory = directory with { Name = change };
        Assert.Throws<IOException>(() => ReplacementPlanStore.ValidateRetirementDirectoryEvidence(plan, directory, topology));
    }
}
