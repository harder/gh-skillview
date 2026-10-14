namespace SkillView.Inventory;

public static class AgentIdentity
{
    public static string Normalize(string id) => id.ToLowerInvariant() switch
    {
        "claude" => "claude-code",
        "copilot" or "github" => "github-copilot",
        "gemini" => "gemini-cli",
        "agents" => "universal",
        var other => other,
    };

    public static bool Matches(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
}
