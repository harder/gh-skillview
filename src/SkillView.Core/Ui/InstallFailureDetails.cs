using System.Text;
using SkillView.Gh.Models;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

// Plain text only: subprocess output must never become Markdown or active links.
internal static class InstallFailureDetails
{
    internal const int MaxFieldCharacters = 8192;

    internal static string Literal(string? value)
    {
        value ??= string.Empty;
        var length = Math.Min(value.Length, MaxFieldCharacters);
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        var text = TerminalEscapeSanitizer.Sanitize(value[..length]) ?? string.Empty;
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
            result.Append(char.IsControl(character) && character is not ('\n' or '\t') ? '\uFFFD' : character);
        if (length < value.Length) result.Append("\n[truncated]");
        return result.ToString();
    }

    internal static string Render(InstallResult result) =>
        $"Install {(result.Succeeded ? "completed" : result.Canceled ? "canceled" : "failed")} (exit {result.ExitCode})\n\n{Literal(result.ErrorMessage)}\n\nStandard error:\n{Literal(result.StdErr)}{(result.StdErrTruncated ? "\n[stderr truncated]" : "")}\n\nStandard output:\n{Literal(result.StdOut)}{(result.StdOutTruncated ? "\n[stdout truncated]" : "")}";

    internal static string Render(InstallAttemptReceipt receipt)
    {
        var text = new StringBuilder(receipt.Guidance);
        if (receipt.Error is { } error) text.Append("\nRefresh: ").Append(Literal(error));
        text.Append("\n\nSelection outcomes (repository-wide children do not prove per-skill outcomes):");
        foreach (var result in receipt.Summary.Results.Take(128))
            text.Append("\n").Append(Literal(result.SkillName ?? "ALL / repository discovery"))
                .Append(": ").Append(result.Attempt).Append(" (exit ").Append(result.ExitCode).Append(')');
        if (receipt.Summary.Results.Count > 128) text.Append("\n[additional child outcomes omitted]");
        text.Append("\n\nNot attempted:\n").Append(Literal(string.Join("\n", receipt.Summary.NotAttempted)));
        // Put the partial selection before verbose child output so a noisy
        // first child cannot hide the later failure or unsubmitted selectors.
        foreach (var result in receipt.Summary.Results.Take(128))
            text.Append("\n\n").Append(Literal(result.Repo)).Append(" / ")
                .Append(Literal(result.SkillName ?? "ALL / repository discovery")).Append("\n").Append(Render(result));
        return Literal(text.ToString());
    }

    internal static Editor CreatePane(string text) => new()
    {
        X = 1,
        Y = 1,
        Width = Dim.Fill(1),
        Height = Dim.Fill(2),
        ReadOnly = true,
        WordWrap = true,
        Text = text,
        CaretOffset = 0
    };

    internal static void Show(IApplication app, string text, Action<Dialog>? readyForTests = null)
    {
        using var dialog = new Dialog
        {
            Title = " Install failure details ",
            Width = Dim.Percent(94),
            Height = Dim.Percent(90),
        };
        var output = CreatePane(text);
        TuiHelpers.ConfigureReadOnlyPane(output, SkillViewStyling.DialogSchemeName);
        var back = new SingleRowButton { X = 1, Y = Pos.AnchorEnd(1), Text = "Back", IsDefault = true };
        back.Accepting += (_, args) => { args.Handled = true; app.RequestStop(); };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Esc) return;
            key.Handled = true; app.RequestStop();
        };
        dialog.Accepting += (_, args) => args.Handled = true;
        dialog.Add(output, back);
        TuiHelpers.ApplyScheme(SkillViewStyling.DialogSchemeName, dialog, back);
        output.SetFocus();
        readyForTests?.Invoke(dialog);
        app.Run(dialog);
    }
}
