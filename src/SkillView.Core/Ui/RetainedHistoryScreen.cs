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
        Action<Action>? dispatchForTests = null,
        Func<RetirementReview, CancellationToken, Task>? retire = null)
    {
        if (lifetime.IsCancellationRequested) return;
        using var requests = new LatestRequestGate(CancellationCallbackReporter.For(logger, "history"));
        using var dialog = new Dialog { Title = " History · retained operations ", Width = Dim.Fill(), Height = Dim.Fill() };
        var status = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 3, Text = "Reading retained operation hints…" };
        var list = new ListView { X = 0, Y = 3, Width = Dim.Fill(), Height = 5 };
        var detail = new Editor { X = 0, Y = 8, Width = Dim.Fill(), Height = Dim.Fill(3), ReadOnly = true, WordWrap = true };
        var refresh = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "_Refresh" };
        var verify = new SingleRowButton { X = Pos.Right(refresh) + 1, Y = Pos.AnchorEnd(1), Text = "_Inspect", Enabled = false };
        var retireButton = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(2), Text = "Retire", Visible = false, Enabled = false };
        RetirementReview? retirement = null;
        void ClearRetirement() { retirement = null; retireButton.Enabled = retireButton.Visible = false; }
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
            ClearRetirement();
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
            ClearRetirement();
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
                    ReplacementResult? inspected = null;
                    ReplacementHistory? captured = null;
                    try
                    {
                        if (inspection)
                        {
                            inspected = await Task.Run(() => inspect(id!, request.Token), request.Token).ConfigureAwait(false);
                            text = RenderInspection(inspected);
                        }
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
                            if (id == selectedId && inspected is not null && inspected.Id == id && inspected.Retirement is { } admitted && retire is not null)
                            {
                                retirement = admitted;
                                retireButton.Text = admitted.Action + "…";
                                retireButton.Visible = retireButton.Enabled = true;
                            }
                            status.Text = "Inspection result for " + id + ". Refresh or Inspect again for fresh evidence.";
                        }
                        verify.Enabled = selectedId is not null && QuarantineStore.ValidId(selectedId);
                    }, request.Token).ConfigureAwait(false);
                }
            }, inspection ? "history.inspect" : "history.refresh")) request.Dispose();
        }
        void Retire()
        {
            if (!active || retirement is not { } review || review.Id != selectedId || retire is null) return;
            // Confirmation consumes the displayed approval even on Cancel.
            ClearRetirement();
            requests.Cancel();
            if (!ConfirmRetirement(app, review) || !active || lifetime.IsCancellationRequested) return;
            var request = requests.Begin(lifetime, TimeSpan.FromMinutes(2));
            refresh.Enabled = verify.Enabled = list.Enabled = false;
            status.Text = "Retiring this exact saved operation… Back/Esc cancels; partial evidence stays protected.";
            if (!runTask(async () =>
            {
                using (request)
                {
                    string outcome;
                    try
                    {
                        await Task.Run(() => retire(review, request.Token), request.Token).ConfigureAwait(false);
                        outcome = "Retired " + review.Id + ". Capacity released after native removal and publication.";
                    }
                    catch (OperationCanceledException) { outcome = "Retirement canceled. Remaining evidence is protected; Inspect again before any retry."; }
                    catch (Exception ex) { outcome = "Retirement not verified: " + Literal(ex.Message) + " Inspect History again; preserve remaining evidence."; }
                    ReplacementHistory? refreshed = null;
                    try { refreshed = await load(lifetime).ConfigureAwait(false); }
                    catch (Exception ex) { outcome += " History refresh failed: " + Literal(ex.Message); }
                    await SkillViewApp.AwaitDispatchAsync(dispatchForTests ?? app.Invoke, () =>
                    {
                        if (!active || lifetime.IsCancellationRequested || !request.IsCurrent) return;
                        refresh.Enabled = list.Enabled = true;
                        ClearRetirement();
                        if (refreshed is not null)
                        {
                            history = refreshed;
                            settingList = true;
                            list.SetSource(new ObservableCollection<string>(refreshed.Entries.Select(entry => InstalledOverview.Text(entry.Skill ?? entry.Id, 48) + " · " + entry.Hint)));
                            list.SelectedItem = refreshed.Entries.Length == 0 ? null : 0;
                            settingList = false;
                            selectedId = Selected()?.Id;
                            observed?.Invoke(refreshed);
                        }
                        verify.Enabled = selectedId is not null && QuarantineStore.ValidId(selectedId);
                        status.Text = "Retirement attempt finished. Inspect for new approval.";
                        SetDetail(outcome);
                    }, lifetime).ConfigureAwait(false);
                }
            }, "history.retire"))
            {
                request.Dispose();
                refresh.Enabled = list.Enabled = true;
                verify.Enabled = selectedId is not null;
            }
        }
        retireButton.Accepting += (_, args) => { args.Handled = true; Retire(); };
        list.ValueChanged += (_, _) => SelectionChanged();
        list.Accepted += (_, args) => { args.Handled = true; Start(true); };
        refresh.Accepting += (_, args) => { args.Handled = true; Start(false); };
        verify.Accepting += (_, args) => { args.Handled = true; Start(true); };
        void Close() { if (!active) return; active = false; requests.Cancel(); app.RequestStop(dialog); }
        back.Accepting += (_, args) => { args.Handled = true; Close(); };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; Close(); } };
        dialog.IsRunningChanging += (_, args) => { if (!args.NewValue) { active = false; requests.Cancel(); } };
        dialog.Add(status, list, detail, refresh, verify, back, retireButton);
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, dialog, status, list, detail, refresh, verify, back, retireButton);
        TuiHelpers.ConfigureReadOnlyPane(detail, SchemeNames.Dialog);
        using var registration = lifetime.Register(() => app.Invoke(Close));
        readyForTests?.Invoke(dialog);
        Start(false);
        try { app.Run(dialog); }
        finally { active = false; requests.Cancel(); }
    }

    internal static bool ConfirmRetirement(IApplication app, RetirementReview review, Action<Dialog>? readyForTests = null)
    {
        var approved = false;
        using var dialog = new Dialog { Title = " " + review.Action + " ", Width = Dim.Percent(95), Height = 16 };
        var text = new Editor
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            WordWrap = true,
            Text = Literal(review.Consequence + "\n\nID: " + review.Id + "\nInspection: " + review.Fingerprint
                + "\n\nThis approval applies only to this saved operation and current inspection. Cancel leaves the data unchanged.")
        };
        var cancel = new SingleRowButton { Text = "_Cancel", IsDefault = true };
        var confirm = new SingleRowButton { Text = review.Action, IsDefault = false };
        cancel.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        confirm.Accepting += (_, args) => { args.Handled = true; approved = true; app.RequestStop(dialog); };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; app.RequestStop(dialog); } };
        dialog.Add(text); dialog.AddButton(cancel); dialog.AddButton(confirm);
        confirm.IsDefault = false;
        cancel.IsDefault = true;
        TuiHelpers.ApplyScheme(SchemeNames.Dialog, dialog, text, cancel, confirm);
        TuiHelpers.ConfigureReadOnlyPane(text, SchemeNames.Dialog);
        readyForTests?.Invoke(dialog);
        cancel.SetFocus();
        app.Run(dialog);
        return approved;
    }

    internal static string RenderEntry(ReplacementHistoryEntry entry) =>
        Literal($"{entry.Hint}\nID: {entry.Id}\nSkill: {entry.Skill ?? "unknown"}\nAction: {entry.Purpose ?? "unknown"}\nSaved: {entry.CreatedAt?.ToString("O") ?? "unknown"}\nSource: {entry.Source ?? "unknown"}\nTarget (saved): {entry.OriginalPath ?? "unknown"}\nScan root (saved): {entry.ScanRoot ?? "unknown"}\nOperation directory: {entry.OperationPath ?? "unknown"}\nSlots (names only, relative to operation directory): {(entry.Slots.IsDefaultOrEmpty ? "unknown/none" : string.Join(", ", entry.Slots))}\nRecorded slot byte estimate (hint, not measured): {entry.RecordedSlotBytes?.ToString() ?? "unknown"}\n{entry.Detail}\nInspect to verify. Retirement is offered only after fresh verification; recovery and Undo use their specialist workflows.");

    internal static string RenderInspection(ReplacementResult result) => Literal(
        $"Explicit inspection · {result.Status}\nID: {result.Id}\nSkill: {result.Subject.Name}\nTarget: {result.OriginalPath}\nRetained original: {result.RetainedPath}\nCandidate: {result.CandidatePath}\nDisplaced: {result.DisplacedPath}\nSaved plan: {result.PlanFingerprint}\nCurrent inspection: {result.ReviewFingerprint}\n{result.Message}\n{(result.Retirement is { } retirement ? "Available: " + retirement.Action + ". " + retirement.Consequence + (retirement.Resuming ? " This resumes a marked retirement after fresh approval." : "") + "\n" : "")}Inspection is a point-in-time observation; it grants no mutation approval. Existing specialist CLI inspection/recovery/undo workflows require their own fresh reviews and approvals.");

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
