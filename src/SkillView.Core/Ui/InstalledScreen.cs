using System.Globalization;
using SkillView.Inventory.Models;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Phase 2 Installed collection. A modal dialog listing the `InstalledSkill`
/// records in a `TableView` with a right-side detail pane. Deliberately
/// modest: Phase 6 adds remove/cleanup actions; Phase 9 refines layout once
/// the TG2 deep-dive pass surfaces better building blocks.
public static class InstalledScreen
{
    public static void Show(IApplication app, InventorySnapshot snapshot, Action<InstalledSkill>? onRemove = null)
    {
        using var dialog = new Dialog
        {
            Title = snapshot.UsedGhSkillList
                ? "Installed — gh skill list + filesystem"
                : "Installed — filesystem scan",
            Width = Dim.Percent(90),
            Height = Dim.Percent(90),
        };

        var table = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(60),
            Height = Dim.Fill(1),
            FullRowSelect = true,
        };
        var rows = snapshot.Skills;
        table.Table = new EnumerableTableSource<InstalledSkill>(
            rows,
            new Dictionary<string, Func<InstalledSkill, object>>
            {
                ["Name"] = s => TuiHelpers.Truncate(s.Name, 28),
                ["Scope"] = s => s.Scope.ToString(),
                ["Source"] = s => s.Provenance.ToString(),
                ["!"] = s => s.Validity == ValidityState.Valid ? "" : "!",
                ["Lnk"] = s => s.IsSymlinked ? "↩" : "",
                ["Agents"] = s => TuiHelpers.Truncate(
                    string.Join(",", s.Agents.Select(a => a.AgentId).Distinct(StringComparer.OrdinalIgnoreCase)),
                    30),
            });

        var detail = new TextView
        {
            X = Pos.Right(table),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            Text = rows.Length == 0 ? "(no skills found)" : RenderDetail(rows[0]),
        };
        TuiHelpers.ConfigureReadOnlyPane(detail, "Dialog");

        table.SelectedCellChanged += (_, _) =>
        {
            var row = table.SelectedRow;
            if (row >= 0 && row < rows.Length)
            {
                detail.Text = RenderDetail(rows[row]);
            }
        };

        var footer = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Text = $" {rows.Length} skill(s) across {snapshot.ScannedRoots.Length} root(s)" +
                   (snapshot.UsedGhSkillList ? " · gh data + scan" : " · scan only") +
                   (onRemove is null ? "   Esc/q close" : "   x remove · Esc/q close"),
        };

        TuiHelpers.ApplyScheme("Dialog", dialog, table, detail, footer);

        dialog.Add(table, detail, footer);
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc || key.AsRune.Value == 'q' || key.AsRune.Value == 'Q')
            {
                app.RequestStop();
                key.Handled = true;
                return;
            }
            if ((key.AsRune.Value == 'x' || key.AsRune.Value == 'X') && onRemove is not null)
            {
                var i = table.SelectedRow;
                if (i >= 0 && i < rows.Length) onRemove(rows[i]);
                key.Handled = true;
            }
        };

        app.Run(dialog);
    }

    internal static string RenderDetail(InstalledSkill s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"name      : {s.Name}");
        sb.AppendLine($"path      : {s.ResolvedPath}");
        sb.AppendLine($"scope     : {s.Scope}");
        sb.AppendLine($"provenance: {s.Provenance}");
        sb.AppendLine($"validity  : {s.Validity}");
        sb.AppendLine($"symlinked : {s.IsSymlinked}");
        sb.AppendLine($"pinned    : {s.Pinned}");
        sb.AppendLine($"ignored   : {s.Ignored}");
        sb.AppendLine($"tree-sha  : {s.TreeSha ?? "(unset)"}");
        sb.AppendLine($"version   : {s.FrontMatter.Version ?? "(unset)"}");
        if (s.InstalledAt is { } when_)
        {
            sb.AppendLine($"installed : {when_.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        }
        if (s.FrontMatter.Description is { Length: > 0 } desc)
        {
            sb.AppendLine();
            sb.AppendLine("description:");
            sb.AppendLine(desc);
        }
        if (s.Agents.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("agents:");
            foreach (var a in s.Agents)
            {
                var kind = a.IsSymlink ? "symlink" : "direct";
                sb.AppendLine($"  - {a.AgentId} ({kind}) {a.Path}");
            }
        }
        return sb.ToString();
    }
}
