using SkillView.Inventory.Models;

namespace SkillView.Ui;

internal static class SkillHealthFormatter
{
    internal static string RowBadge(ValidityState validity, bool isSymlinked) =>
        isSymlinked
            ? "Symlink"
            : validity == ValidityState.Valid
                ? "Healthy"
                : "Needs review";
}
