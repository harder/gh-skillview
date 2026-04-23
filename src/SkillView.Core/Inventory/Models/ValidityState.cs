namespace SkillView.Inventory.Models;

/// Per-install validation state. `Malformed` surfaces instead of hiding,
/// per §10.3 ("malformed items surface with a warning rather than silent
/// hiding").
public enum ValidityState
{
    Valid,
    MissingSkillMd,
    UnparsableFrontMatter,
    NameMismatch,
    BrokenSymlink,
}
