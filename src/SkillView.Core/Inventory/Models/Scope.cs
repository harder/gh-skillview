namespace SkillView.Inventory.Models;

/// Classifies a scan root. `Custom` covers `--scan-root` overrides.
public enum Scope
{
    Project,
    User,
    Custom,
}
