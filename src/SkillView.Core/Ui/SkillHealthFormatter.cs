using SkillView.Inventory.Models;

namespace SkillView.Ui;

internal static class SkillHealthFormatter
{
    internal static string RowBadge(ValidityState validity, bool isSymlinked) =>
        validity != ValidityState.Valid
            ? "Needs review"
            : isSymlinked
            ? "Symlink"
            : validity == ValidityState.Valid
                ? "Healthy"
                : "Needs review";

    internal static string CompactBadge(ValidityState validity, bool isSymlinked) =>
        validity != ValidityState.Valid
            ? "REV"
            : isSymlinked
            ? "SYM"
            : validity == ValidityState.Valid
                ? "OK"
                : "REV";
}
