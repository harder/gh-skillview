using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui.Theming;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Selected-copy detail embedded in a main view. No inventory capture, payload
/// work or admission takes place in rendering. The main view owns return state.
internal sealed class InlineCopiesView : View
{
    private readonly Func<Action, Task> _dispatch;
    private readonly Action<Func<Task>, string> _run;
    private readonly Func<long> _generation;
    private readonly Func<string> _context;
    private readonly CancellationToken _lifetime;
    private readonly LatestRequestGate _requests;
    private readonly Func<InstalledSkill, InstalledSkill, CancellationToken, Task<SkillComparisonResult>> _comparePayloads;
    internal InlineCopyRequest? Request => _request;
    private readonly Markdown _detail;
    private readonly Button _choose;
    private readonly Button _compare;
    private readonly Button _back;
    private readonly Action _return;
    private PopoverMenu? _menu;
    private InlineCopyRequest? _request;
    private InstalledSkill? _reference;
    private CapturedCopyComparison? _fact;
    private long _epoch;
    private bool _disposed;
    private bool _busy;
    private string _notice = "";
    internal bool Busy => _busy;
    internal bool Open => _request is not null;
    internal CapturedCopyComparison? Fact => Current(_request) && _fact?.Matches(_request!, _reference) == true ? _fact : null;
    internal string TextForTests => _detail.Text;

    internal InlineCopiesView(Func<Action, Task> dispatch, Action<Func<Task>, string> run,
        Func<long> generation, Func<string> context, Action back, CancellationToken lifetime,
        Action<AggregateException>? report = null, Func<InstalledSkill, InstalledSkill, CancellationToken, Task<SkillComparisonResult>>? compare = null)
    {
        _comparePayloads = compare ?? ((left, right, token) => new SkillComparisonService().CompareAsync(left, right, token));
        _dispatch = dispatch; _run = run; _generation = generation; _context = context; _return = back; _lifetime = lifetime;
        _requests = new(report);
        CanFocus = true;
        Visible = false;
        _detail = new Markdown { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2) };
        TuiHelpers.ConfigureMarkdownPane(_detail, SchemeNames.Base);
        _choose = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(2), Text = "Reference..." };
        _compare = new SingleRowButton { X = Pos.Right(_choose) + 1, Y = Pos.AnchorEnd(2), Text = "Compare" };
        _back = new SingleRowButton { X = 0, Y = Pos.AnchorEnd(1), Text = "Back (Esc)" };
        _choose.Accepting += (_, e) => { e.Handled = true; ChooseReference(); };
        _compare.Accepting += (_, e) => { e.Handled = true; Compare(); };
        _back.Accepting += (_, e) => { e.Handled = true; _return(); };
        KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc) { key.Handled = true; _return(); }
            else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value == '=') { key.Handled = true; Compare(); }
        };
        Add(_detail, _choose, _compare, _back);
        Disposing += (_, _) => { _disposed = true; Retire(); _requests.Dispose(); };
    }

    internal bool Show(InventorySnapshot snapshot, InstalledSkill target, object returnState, FindingBinding? binding = null)
    {
        Retire();
        _request = InlineCopyRequest.Capture(snapshot, _generation(), _context(), ++_epoch, target, returnState, binding);
        if (_request is null) return false;
        _notice = "Choose a reference explicitly, then Compare. Reference is for comparison only; it is never a survivor.";
        Visible = true;
        Render();
        _choose.SetFocus();
        return true;
    }

    internal void ValidateObservation()
    {
        if (_request is not null && !Current(_request))
        {
            _requests.Cancel(); _fact = null; _reference = null; _busy = false;
            _notice = "Inventory or context changed; Back and refresh before comparing again.";
            _choose.Enabled = _compare.Enabled = false;
            _detail.Text = MarkdownTableFormatter.FormatCodeSpan(_notice);
        }
    }

    internal void Retire()
    {
        _epoch++;
        _requests.Cancel();
        _request = null; _reference = null; _fact = null; _busy = false;
        _menu?.Dispose(); _menu = null;
        Visible = false;
    }

    private bool Current(InlineCopyRequest? request) => request is not null && ReferenceEquals(request, _request)
        && request.Epoch == _epoch && !_disposed && !_lifetime.IsCancellationRequested && Visible
        && request.Generation == _generation() && request.Context == _context() && request.HasExactTarget;

    private void ChooseReference()
    {
        if (!Current(_request)) return;
        var request = _request!;
        _menu?.Dispose();
        var choices = request.Choices.Where(copy => !ReferenceEquals(copy, request.Target)).ToArray();
        var labels = DistinguishingPathLabels.Build(choices.Select(copy => copy.ResolvedPath), request.Target.Name,
            Math.Max(12, App!.Screen.Width - 6), CancellationToken.None);
        _menu = AnchoredActions.Show(_choose, choices.Select(copy => (labels[copy.ResolvedPath], (Action)(() => SetReference(request, copy)))));
    }

    internal void SetReference(InlineCopyRequest request, InstalledSkill copy)
    {
        if (!Current(request) || !request.Choices.Any(choice => ReferenceEquals(choice, copy)) || ReferenceEquals(copy, request.Target)) return;
        _requests.Cancel(); _fact = null; _busy = false;
        _reference = copy;
        _notice = "Reference selected for comparison only. Select Compare to read bounded local payloads.";
        Render(); _compare.SetFocus();
    }

    private void Compare()
    {
        if (!Current(_request) || _reference is null || _busy) return;
        var request = _request!; var reference = _reference;
        var lease = _requests.Begin(_lifetime, TimeSpan.FromMinutes(2));
        _fact = null; _busy = true; _notice = "Comparing local files… Back cancels."; Render();
        _run(async () =>
        {
            using (lease)
            {
                try
                {
                    var result = await _comparePayloads(reference, request.Target, lease.Token).ConfigureAwait(false);
                    await _dispatch(() =>
                    {
                        if (!lease.IsCurrent || lease.Token.IsCancellationRequested || !Current(request) || !ReferenceEquals(reference, _reference)) return;
                        _fact = new(request, reference, result, DateTimeOffset.UtcNow);
                        _busy = false; _notice = _fact.Guidance; Render();
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lease.Token.IsCancellationRequested || _lifetime.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    await _dispatch(() =>
                    {
                        if (!lease.IsCurrent || !Current(request)) return;
                        _busy = false; _fact = null; _notice = "Comparison unavailable: " + TuiHelpers.ErrorSnippet(ex.Message); Render();
                    }).ConfigureAwait(false);
                }
            }
        }, "copies.inline-compare");
    }

    private void Render()
    {
        if (_request is not { } request) return;
        _choose.Enabled = request.Choices.Length > 1;
        _compare.Enabled = !_busy && _reference is not null;
        var group = new CopiesGroup(request.Choices.ToArray(), request.Omitted, request.Ambiguous, request.Snapshot.IsPartial || !request.Snapshot.ConfiguredRootsComplete);
        var text = CopiesPresentation.Render(group, _reference, request.Target, Fact?.Result);
        // Preserve the pure formatter's bounded literal fields while allowing
        // long exact locations to wrap at the current Markdown viewport width.
        _detail.Text = "## Selected copy comparison\n\n"
            + (request.Finding is null ? "Compare copies (broad: same name or recorded source; not the finding's family)\n\n" : "Exact captured finding members only.\n\n") + MarkdownTableFormatter.FormatCodeSpan(_notice)
            + "\n\n" + string.Join("\n\n", text.Split('\n').Where(line => line.Length > 0).Select(MarkdownTableFormatter.FormatCodeSpan))
            + "\n\nLocal capture success does not establish complete inventory or all consumers. Full ACL/xattr/ADS equivalence and running sessions remain unknown. Comparison grants no mutation authority. Tracked copies cannot use standalone Move aside.\n";
    }
}
