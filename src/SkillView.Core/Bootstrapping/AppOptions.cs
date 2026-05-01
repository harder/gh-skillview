namespace SkillView.Bootstrapping;

/// Parsed global flags and invocation context. Immutable record rather than mutable config.
public sealed record AppOptions(
    InvocationMode InvocationMode,
    DispatchMode DispatchMode,
    bool Debug,
    AppTheme Theme,
    IReadOnlyList<string> ScanRoots,
    string? SubcommandName,
    IReadOnlyList<string> SubcommandArgs
);
