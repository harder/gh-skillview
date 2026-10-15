using System.Collections.Immutable;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Loading;
using SkillView.Logging;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Local-file analysis only; the modal owns and drains every capture before
/// controls are disposed. Changing context never edits agent configuration.
internal static class EffectiveLoadingScreen
{
    internal static ImmutableArray<EffectiveSkillCandidate> SelectInstallCandidates(EffectiveSkillAnalysis analysis, InstalledSkill selected)
    {
        var id = selected.ObservedSubject?.InstallationId;
        return analysis.Candidates.Where(candidate => id is not null
            ? string.Equals(candidate.Subject.InstallationId, id, StringComparison.Ordinal)
            : string.Equals(candidate.Subject.Path, selected.ResolvedPath, StringComparison.Ordinal)).ToImmutableArray();
    }

    internal static void Show(IApplication app, Logger logger, InstalledSkill selected, string workingDirectory,
        Func<LoadingCaptureRequest, CancellationToken, Task<EffectiveSkillAnalysis>> capture,
        Action<Dialog>? readyForTests = null)
    {
        using var dialog = new Dialog { Title = " Skill loading ", Width = Dim.Percent(96), Height = Dim.Percent(94), SchemeName = SchemeNames.Dialog };
        using var operation = new ModalOperationTracker(app, logger, "effective.ui");
        var form = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = 9, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
        TextField Field(int row, string label, string initial)
        {
            var caption = new Label { X = 0, Y = row, Text = label };
            var field = new TextField { X = 22, Y = row, Width = Dim.Fill(2), Text = initial };
            TuiHelpers.ConfigureTextInput(field, SchemeNames.Dialog);
            form.Add(caption, field); return field;
        }
        var defaultAgent = selected.Agents.Select(member => AgentIdentity.Normalize(member.AgentId))
            .FirstOrDefault(id => id is "claude-code" or "codex" or "gemini-cli" or "opencode") ?? "codex";
        var agent = Field(0, "Agent:", defaultAgent);
        var profile = Field(1, "Profile (optional):", "");
        var project = Field(2, "Project:", workingDirectory);
        var cwd = Field(3, "Working directory:", workingDirectory);
        var config = Field(4, "Config dir (optional):", "");
        var version = Field(5, "Client version:", "");
        var role = Field(6, "OpenCode agent:", "");
        var trust = Field(7, "Trust assumption:", "unknown");
        form.Add(new Label { X = 0, Y = 8, Width = Dim.Fill(), Text = "Trust: unknown / trusted / untrusted. Version labels do not prove native compatibility." });
        var status = new Label { X = 0, Y = 9, Width = Dim.Fill(), Text = "Select a context and Analyze. Native/session observations stay separate from this preview." };
        var output = new Editor
        {
            X = 0,
            Y = 11,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            WordWrap = true,
            Text = "Filesystem presence and symlinks do not establish that a skill is loaded.\n\nThis view reads bounded configuration and metadata without launching an AI client."
        };
        var analyze = new Button { Text = "_Analyze", X = 0, Y = Pos.AnchorEnd(1) };
        var close = new Button { Text = "_Close", X = Pos.Right(analyze) + 2, Y = Pos.AnchorEnd(1) };
        var closeRequested = false;
        var readyToClose = false;
        static string? Optional(TextField field) => string.IsNullOrWhiteSpace(field.Text) ? null : field.Text.Trim();
        analyze.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None) return;
            var request = new LoadingCaptureRequest(agent.Text.Trim(), Optional(profile), Optional(project), Optional(cwd), Optional(config), Optional(version), Optional(role), trust.Text.Trim());
            analyze.Enabled = false; form.Enabled = false; status.Text = "Analyzing configuration and inventory…";
            operation.TryStart(async token =>
            {
                try
                {
                    var result = await capture(request, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    // Compare captured strings only; rendering never probes a
                    // path to recover a physical/context key on the UI thread.
                    var selectedRows = SelectInstallCandidates(result, selected);
                    var relevant = selectedRows.Select(candidate => candidate.LoadingKey).Concat(selectedRows.SelectMany(candidate => candidate.SelectionPeers)).ToHashSet(StringComparer.Ordinal);
                    var rendered = CliDispatcher.RenderEffectiveText(result with { Candidates = result.Candidates.Where(candidate => relevant.Contains(candidate.LoadingKey)).ToImmutableArray() });
                    operation.InvokeTerminalIfActive(() =>
                    {
                        output.Text = rendered; status.Text = $"{selectedRows.Length} selected-install entry/command candidate(s), with relevant competitors. Native: {result.Native.State}; session: unknown.";
                        Finish();
                    });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { operation.InvokeTerminalIfActive(() => { status.Text = "Analysis canceled."; Finish(); }); }
                catch (Exception exception)
                {
                    logger.Error("effective.ui", exception.Message);
                    operation.InvokeTerminalIfActive(() => { status.Text = TerminalEscapeSanitizer.Sanitize("Analysis failed: " + exception.Message) ?? "Analysis failed."; Finish(); });
                }
            });
        };
        close.Accepting += (_, args) => { args.Handled = true; RequestClose(); };
        dialog.IsRunningChanging += (_, args) =>
        {
            if (args.NewValue || readyToClose || operation.CurrentOwnership == ModalOperationTracker.Ownership.None) return;
            args.Cancel = true; closeRequested = true; operation.Cancel(); status.Text = "Canceling analysis…";
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Esc) return;
            key.Handled = true; RequestClose();
        };
        void RequestClose()
        {
            if (operation.CurrentOwnership != ModalOperationTracker.Ownership.None)
            { closeRequested = true; operation.Cancel(); status.Text = "Canceling analysis…"; return; }
            readyToClose = true; app.RequestStop();
        }
        void Finish()
        {
            operation.Release();
            if (closeRequested) { readyToClose = true; app.RequestStop(); return; }
            form.Enabled = true; analyze.Enabled = true;
        }
        dialog.Add(form, status, output, analyze, close);
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, dialog, form, agent, profile, project, cwd, config, version, role, trust, status, output, analyze, close);
        TuiHelpers.ConfigureReadOnlyPane(output, SchemeNames.Dialog);
        readyForTests?.Invoke(dialog);
        app.Run(dialog);
    }
}
