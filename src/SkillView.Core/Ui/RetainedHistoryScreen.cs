using System.Collections.ObjectModel;
using System.Text;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Threading;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

internal static class RetainedHistoryScreen
{
    internal static void Show(IApplication app, Logger logger,
        Func<CancellationToken, Task<ReplacementHistory>> load,
        Func<string, CancellationToken, Task<ReplacementResult>> inspect,
        Func<Func<Task>, string, bool> runTask, CancellationToken lifetime,
        Action<ReplacementHistory>? observed = null, Action<Dialog>? readyForTests = null,
        Action<Action>? dispatchForTests = null)
    {
        if (lifetime.IsCancellationRequested) return;
        using var requests = new LatestRequestGate(CancellationCallbackReporter.For(logger, "history"));
        using var dialog = new Dialog { Title = " History · retained operations · read-only ", Width = Dim.Fill(), Height = Dim.Fill() };
        var status = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 3, Text = "Reading retained operation hints…" };
        var list = new ListView { X = 0, Y = 3, Width = Dim.Fill(), Height = 5 };
        var detail = new Editor { X = 0, Y = 8, Width = Dim.Fill(), Height = Dim.Fill(2), ReadOnly = true, WordWrap = true };
        var refresh = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "_Refresh" };
        var verify = new SingleRowButton { X = Pos.Right(refresh) + 1, Y = Pos.AnchorEnd(1), Text = "_Inspect", Enabled = false };
        var back = new SingleRowButton { X = Pos.Right(verify) + 1, Y = Pos.AnchorEnd(1), Text = "_Back" };
        void SetDetail(string text)
        {
            detail.Text = text;
            detail.Layout();
            detail.CaretOffset = 0;
            detail.Viewport = detail.Viewport with { X = 0, Y = 0 };
        }
        var active = true;
        ReplacementHistory? history = null;
        string? selectedId = null;
        bool settingList = false;
        ReplacementHistoryEntry? Selected() => list.SelectedItem is { } index && history is not null && index >= 0 && index < history.Entries.Length
            ? history.Entries[index] : null;
        void SelectionChanged()
        {
            if (settingList) return;
            requests.Cancel();
            var entry = Selected();
            selectedId = entry?.Id;
            if (history is not null) status.Text = Literal(history.Summary, 2000);
            verify.Enabled = entry is not null && QuarantineStore.ValidId(entry.Id);
            SetDetail(entry is null ? "No retained replacement operations. Back returns to your previous view."
                : RenderEntry(entry));
        }
        void Start(bool inspection)
        {
            if (!active || lifetime.IsCancellationRequested) return;
            var id = selectedId;
            if (inspection && (id is null || !QuarantineStore.ValidId(id))) return;
            var request = requests.Begin(lifetime, TimeSpan.FromMinutes(2));
            verify.Enabled = false;
            // Every refresh/inspection clears the previous verified result.
            SetDetail(inspection ? "Inspecting current saved locations and native payload evidence…" : "Refreshing hints; previous inspection cleared…");
            status.Text = inspection ? "Inspect runs full local verification; cancellation is available with Back/Esc." : "Reading bounded metadata and entry names…";
            if (!runTask(async () =>
            {
                using (request)
                {
                    string? text = null;
                    ReplacementHistory? captured = null;
                    try
                    {
                        if (inspection) text = await Task.Run(async () => RenderInspection(await inspect(id!, request.Token).ConfigureAwait(false)), request.Token).ConfigureAwait(false);
                        else captured = await Task.Run(() => load(request.Token), request.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
                    {
                        await SkillViewApp.AwaitDispatchAsync(dispatchForTests ?? app.Invoke, () =>
                        {
                            if (!active || lifetime.IsCancellationRequested || !request.IsCurrent) return;
                            status.Text = "History request canceled or timed out. Refresh or Back to continue.";
                            SetDetail("Previous inspection cleared; Inspect to verify again.");
                            verify.Enabled = selectedId is not null && QuarantineStore.ValidId(selectedId);
                        }, lifetime).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) { text = "Protected — Inspect could not verify this operation.\n" + Literal(ex.Message); }
                    await SkillViewApp.AwaitDispatchAsync(dispatchForTests ?? app.Invoke, () =>
                    {
                        if (!active || lifetime.IsCancellationRequested || !request.IsCurrent) return;
                        if (captured is not null)
                        {
                            history = captured;
                            settingList = true;
                            list.SetSource(new ObservableCollection<string>(captured.Entries.Select(entry => InstalledOverview.Text(entry.Skill ?? entry.Id, 48) + " · " + entry.Hint)));
                            var retained = Array.FindIndex(captured.Entries.ToArray(), entry => entry.Id == id);
                            list.SelectedItem = captured.Entries.Length == 0 ? null : Math.Max(0, retained);
                            settingList = false;
                            selectedId = Selected()?.Id;
                            SetDetail(Selected() is { } entry ? RenderEntry(entry) : "No retained replacement operations were listed.");
                            status.Text = Literal(captured.Summary, 2000);
                            observed?.Invoke(captured);
                        }
                        else
                        {
                            SetDetail(text ?? "Inspection canceled; refresh to read current hints.");
                            status.Text = "Inspection result for " + id + ". Refresh or Inspect again for fresh evidence.";
                        }
                        verify.Enabled = selectedId is not null && QuarantineStore.ValidId(selectedId);
                    }, request.Token).ConfigureAwait(false);
                }
            }, inspection ? "history.inspect" : "history.refresh")) request.Dispose();
        }
        list.ValueChanged += (_, _) => SelectionChanged();
        list.Accepted += (_, args) => { args.Handled = true; Start(true); };
        refresh.Accepting += (_, args) => { args.Handled = true; Start(false); };
        verify.Accepting += (_, args) => { args.Handled = true; Start(true); };
        void Close() { if (!active) return; active = false; requests.Cancel(); app.RequestStop(dialog); }
        back.Accepting += (_, args) => { args.Handled = true; Close(); };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; Close(); } };
        dialog.IsRunningChanging += (_, args) => { if (!args.NewValue) { active = false; requests.Cancel(); } };
        dialog.Add(status, list, detail, refresh, verify, back);
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, dialog, status, list, detail, refresh, verify, back);
        TuiHelpers.ConfigureReadOnlyPane(detail, SchemeNames.Dialog);
        using var registration = lifetime.Register(() => app.Invoke(Close));
        readyForTests?.Invoke(dialog);
        Start(false);
        try { app.Run(dialog); }
        finally { active = false; requests.Cancel(); }
    }

    internal static string RenderEntry(ReplacementHistoryEntry entry) =>
        Literal($"{entry.Hint}\nID: {entry.Id}\nSkill: {entry.Skill ?? "unknown"}\nAction: {entry.Purpose ?? "unknown"}\nSaved: {entry.CreatedAt?.ToString("O") ?? "unknown"}\nSource: {entry.Source ?? "unknown"}\nTarget (saved): {entry.OriginalPath ?? "unknown"}\nScan root (saved): {entry.ScanRoot ?? "unknown"}\nOperation directory: {entry.OperationPath ?? "unknown"}\nSlots (names only, relative to operation directory): {(entry.Slots.IsDefaultOrEmpty ? "unknown/none" : string.Join(", ", entry.Slots))}\nRecorded slot byte estimate (hint, not measured): {entry.RecordedSlotBytes?.ToString() ?? "unknown"}\n{entry.Detail}\nInspect to verify. This read-only view cannot resume, undo or retire operations.");

    internal static string RenderInspection(ReplacementResult result) => Literal(
        $"Explicit inspection · {result.Status}\nID: {result.Id}\nSkill: {result.Subject.Name}\nTarget: {result.OriginalPath}\nRetained original: {result.RetainedPath}\nCandidate: {result.CandidatePath}\nDisplaced: {result.DisplacedPath}\nSaved plan: {result.PlanFingerprint}\nCurrent inspection: {result.ReviewFingerprint}\n{result.Message}\nInspection is a point-in-time observation; it grants no mutation approval. Existing specialist CLI inspection/recovery/undo workflows require their own fresh reviews and approvals.");

    internal static string Literal(string value, int limit = 32000)
    {
        var length = Math.Min(value.Length, limit);
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        var text = TerminalEscapeSanitizer.Sanitize(value[..length])!;
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
            result.Append(char.IsControl(character) && character is not ('\n' or '\t') ? '\uFFFD' : character);
        if (length < value.Length) result.Append(" [truncated]");
        return result.ToString();
    }
}
