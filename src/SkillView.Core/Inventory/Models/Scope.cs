namespace SkillView.Inventory.Models;

/// Classifies a scan root per §10.1. `Custom` covers `--scan-root` overrides.
public enum Scope
{
    Project,
    User,
    Custom,
}
