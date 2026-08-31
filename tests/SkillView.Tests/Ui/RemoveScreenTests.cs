using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class RemoveScreenTests
{
    [Fact]
    public void HasSameSafetyContract_RejectsChangedDirectoryOrLinkAuthority()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "skillview-remove-screen-authority",
            "demo");
        var directoryIdentity = new SecureFileIdentity(
            Volume: 1,
            FileIdLow: 2,
            FileIdHigh: 3,
            CanonicalPath: directoryPath,
            IsDirectory: true,
            IsReparsePoint: false,
            ChangeTimeSeconds: 4,
            ChangeTimeNanoseconds: 5,
            WindowsCreationTime: 6,
            WindowsChangeTime: 7);
        var directoryValidation = Validation(directoryPath) with
        {
            ExecutionIdentity = directoryIdentity,
        };
        var displayedDirectory = Evaluation(
            RemoveTargetKind.CurrentInstall,
            directoryValidation);

        Assert.True(RemoveScreen.HasSameSafetyContract(
            displayedDirectory,
            displayedDirectory));
        Assert.False(RemoveScreen.HasSameSafetyContract(
            displayedDirectory,
            WithValidation(
                displayedDirectory,
                directoryValidation with
                {
                    ExecutionIdentity = directoryIdentity with
                    {
                        FileIdHigh = 30,
                    },
                })));

        var parentIdentity = directoryIdentity with
        {
            CanonicalPath = Path.GetDirectoryName(directoryPath)!,
        };
        var linkIdentity = directoryIdentity with
        {
            CanonicalPath = directoryPath,
            IsDirectory = false,
            IsReparsePoint = true,
        };
        var executionLink = new SecureLinkIdentity(
            parentIdentity,
            linkIdentity,
            Path.GetFileName(directoryPath));
        var linkValidation = Validation(directoryPath) with
        {
            ExecutionLinkIdentity = executionLink,
            RemovesLinkOnly = true,
        };
        var displayedLink = Evaluation(
            RemoveTargetKind.AgentSymlink,
            linkValidation);

        Assert.True(RemoveScreen.HasSameSafetyContract(
            displayedLink,
            displayedLink));
        Assert.False(RemoveScreen.HasSameSafetyContract(
            displayedLink,
            WithValidation(
                displayedLink,
                linkValidation with
                {
                    ExecutionLinkIdentity = executionLink with
                    {
                        LinkIdentity = linkIdentity with
                        {
                            ChangeTimeNanoseconds = 50,
                        },
                    },
                })));
        Assert.False(RemoveScreen.HasSameSafetyContract(
            displayedDirectory,
            WithValidation(
                displayedDirectory,
                directoryValidation with
                {
                    RequiresEmptyDirectory = true,
                })));
    }

    private static RemoveTargetEvaluation WithValidation(
        RemoveTargetEvaluation evaluation,
        RemoveValidator.RemoveValidation validation) =>
        evaluation with
        {
            Items =
            [
                new RemoveTargetItem(
                    evaluation.Items.Single().Skill,
                    validation),
            ],
        };

    private static RemoveTargetEvaluation Evaluation(
        RemoveTargetKind kind,
        RemoveValidator.RemoveValidation validation)
    {
        var skill = new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = validation.ResolvedPath,
            ScanRoot = Path.GetDirectoryName(validation.ResolvedPath)!,
            Scope = Scope.User,
            Agents = ImmutableArray<AgentMembership>.Empty,
            FrontMatter = new SkillFrontMatter { Name = "demo" },
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = kind == RemoveTargetKind.AgentSymlink,
            InstalledAt = null,
        };
        var target = new RemoveTarget(
            kind,
            "Remove demo",
            "Remove the selected install.",
            [skill]);
        return new RemoveTargetEvaluation(
            target,
            [new RemoveTargetItem(skill, validation)]);
    }

    private static RemoveValidator.RemoveValidation Validation(string path) =>
        new(
            ImmutableArray<RemoveValidator.Error>.Empty,
            ImmutableArray<RemoveValidator.Warning>.Empty,
            path,
            ImmutableArray<string>.Empty);
}
