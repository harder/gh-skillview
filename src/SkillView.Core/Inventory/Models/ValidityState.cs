namespace SkillView.Inventory.Models;

/// Per-install validation state. `Malformed` surfaces instead of hiding so
/// broken entries stay visible to the user.
public enum ValidityState
{
    Valid,
    MissingSkillMd,
    UnparsableFrontMatter,
    NameMismatch,
    BrokenSymlink,
}
