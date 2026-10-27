using System.Drawing;
using SkillView.Subprocess;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

// Config/root detection is an observation, never a claim about active clients.
internal sealed class InstallAgentSelection
{
    private readonly View _view;
    private readonly HashSet<string> _detected = new(StringComparer.Ordinal);
    private bool _more;
    private bool _layingOut;
    internal CheckBox[] Boxes { get; }
    internal Button More { get; }
    internal event Action? Changed;

    internal InstallAgentSelection(View view)
    {
        _view = view;
        view.CanFocus = true;
        var layout = AgentCheckboxGrid.Build(InstallAgentCatalog.Entries, _detected, 2);
        Boxes = layout.Boxes;
        More = new SingleRowButton { X = 0, Y = 0, Text = "More agents" };
        More.Accepting += (_, e) => { e.Handled = true; _more = !_more; Reflow(); };
        view.Add(More);
        void FollowFocus(View control)
        {
            control.HasFocusChanged += (_, _) =>
            {
                if (!control.HasFocus) return;
                var viewport = view.Viewport;
                var top = control.Frame.Top;
                var offset = top < viewport.Y ? top : top >= viewport.Bottom ? top - viewport.Height + 1 : viewport.Y;
                if (offset != viewport.Y) view.Viewport = viewport with { Y = Math.Max(0, offset) };
            };
        }
        FollowFocus(More);
        foreach (var box in Boxes)
        {
            box.KeyBindings.ReplaceCommands(KeyCode.Enter, Command.Activate);
            view.Add(box);
            FollowFocus(box);
            box.ValueChanged += (_, _) => { Reflow(); Changed?.Invoke(); };
        }
        view.FrameChanged += (_, _) => Reflow();
        Reflow();
    }

    internal void SetDetected(IReadOnlySet<string> detected)
    {
        _detected.UnionWith(detected);
        for (var i = 0; i < Boxes.Length; i++)
            Boxes[i].Value = detected.Contains(InstallAgentCatalog.Entries[i].GhId) ? CheckState.Checked : CheckState.UnChecked;
        Reflow();
    }

    private void Reflow()
    {
        if (_layingOut) return;
        _layingOut = true;
        try
        {
            var ordered = Enumerable.Range(0, Boxes.Length)
                .OrderBy(i => _detected.Contains(InstallAgentCatalog.Entries[i].GhId) ? 0 : 1)
                .ThenBy(i => i).ToArray();
            var colWidth = InstallAgentCatalog.Entries.Max(entry => entry.Label.Length) + 4;
            var columns = Math.Clamp(_view.Viewport.Width / colWidth, 1, 3);
            var visible = 0;
            foreach (var i in ordered)
            {
                var box = Boxes[i];
                var show = _more || _detected.Contains(InstallAgentCatalog.Entries[i].GhId)
                    || box.Value == CheckState.Checked || InstallAgentCatalog.Entries[i].GhId == "universal";
                if (!show && box.HasFocus) More.SetFocus();
                box.Visible = show;
                if (!box.Visible) continue;
                box.X = visible % columns * colWidth;
                box.Y = 1 + visible / columns;
                visible++;
            }
            More.Text = _more ? "Fewer agents" : "More agents";
            _view.SetContentSize(new Size(Math.Max(1, colWidth * columns), 1 + (visible + columns - 1) / columns));
        }
        finally { _layingOut = false; }
    }

    internal IReadOnlyList<string> Selected => InstallAgentCatalog.Entries
        .Where((_, i) => Boxes[i].Value == CheckState.Checked).Select(entry => entry.GhId).ToArray();

    internal static HashSet<string> Detect(ProcessRunner context)
    {
        var home = context.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        home = Path.GetFullPath(home, context.EffectiveWorkingDirectory);
        string? Config(string key) => context.GetEffectiveEnvironmentVariable(key) is { Length: > 0 } path
            ? Path.GetFullPath(path, context.EffectiveWorkingDirectory) : null;
        return InstallAgentCatalog.DetectInstalledGhIds(home, Config("PI_CODING_AGENT_DIR"), Config("CLAUDE_CONFIG_DIR"));
    }
}
