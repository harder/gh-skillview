using System.Globalization;
using SkillView.Inventory.Models;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Phase 2 Installed collection. A full-screen view listing the
/// `InstalledSkill` records in a `TableView` with a right-side detail pane.
/// Replaces the main view entirely so the search results / preview don't
/// bleed through. Esc/q returns to the main view.
public static class InstalledScreen
{
    public static void Show(IApplication app, InventorySnapshot snapshot, Action<InstalledSkill>? onRemove = null)
    {
        using var window = new Window
        {
            Title = snapshot.UsedGhSkillList
                ? "Installed — gh skill list + filesystem"
                : "Installed — filesystem scan",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var table = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(60),
            Height = Dim.Fill(2),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(table);
        var rows = snapshot.Skills;

        void RebuildSource()
        {
            var prevRow = table.SelectedRow;
            var viewportWidth = table.Viewport.Width;
            var available = viewportWidth > 0
                ? Math.Max(40, viewportWidth - 6 /* col separators */)
                : 70;
            // Scope/Source/!/Lnk are short enums/flags; budget them as fixed.
            // Name and Agents share the proportional remainder.
            var fixedCols = 6 /*Scope*/ + 8 /*Source*/ + 1 /*!*/ + 3 /*Lnk*/;
            var remaining = Math.Max(20, available - fixedCols);
            var nameW = Math.Max(12, (int)Math.Round(remaining * 0.45));
            var agentsW = Math.Max(10, remaining - nameW);
            table.Table = new EnumerableTableSource<InstalledSkill>(
                rows,
                new Dictionary<string, Func<InstalledSkill, object>>
                {
                    ["Name"] = s => TuiHelpers.Truncate(s.Name, nameW),
                    ["Scope"] = s => s.Scope.ToString(),
                    ["Source"] = s => s.Provenance.ToString(),
                    ["!"] = s => s.Validity == ValidityState.Valid ? "" : "!",
                    ["Lnk"] = s => s.IsSymlinked ? "↩" : "",
                    ["Agents"] = s => TuiHelpers.Truncate(
                        string.Join(",", s.Agents.Select(a => a.AgentId).Distinct(StringComparer.OrdinalIgnoreCase)),
                        agentsW),
                });
            var style = table.Style;
            style.ExpandLastColumn = true;
            for (var i = 0; i < table.Table.Columns; i++)
            {
                var cs = style.GetOrCreateColumnStyle(i);
                switch (table.Table.ColumnNames[i])
                {
                    case "Name": cs.MinWidth = 8; cs.MaxWidth = nameW; break;
                    case "Agents": cs.MinWidth = 6; break;
                }
            }
            if (prevRow >= 0 && prevRow < rows.Length) table.SelectedRow = prevRow;
            table.Update();
        }

        RebuildSource();
        var lastWidth = -1;
        table.FrameChanged += (_, _) =>
        {
            var w = table.Viewport.Width;
            if (w > 0 && w != lastWidth)
            {
                lastWidth = w;
                RebuildSource();
            }
        };

        var detail = new Markdown
        {
            X = Pos.Right(table),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Text = rows.Length == 0 ? "(no skills found)" : RenderDetail(rows[0]),
        };
        TuiHelpers.ConfigureMarkdownPane(detail, "Base");

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
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Text = $" {rows.Length} skill(s) across {snapshot.ScannedRoots.Length} root(s)" +
                   (snapshot.UsedGhSkillList ? " · gh data + scan" : " · scan only"),
        };

        var statusBar = new StatusBar(onRemove is null
            ? new[]
            {
                new Shortcut { Title = "o", HelpText = "Open" },
                new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Back" },
                new Shortcut { Title = "q", HelpText = "Quit" },
            }
            : new[]
            {
                new Shortcut { Title = "x", HelpText = "Remove" },
                new Shortcut { Title = "o", HelpText = "Open" },
                new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Back" },
                new Shortcut { Title = "q", HelpText = "Quit" },
            });

        TuiHelpers.ApplyScheme("Base", window, table, detail, footer, statusBar);

        window.Add(table, detail, footer, statusBar);
        window.KeyDown += (_, key) =>
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
            else if (key.AsRune.Value == 'o' || key.AsRune.Value == 'O')
            {
                var i = table.SelectedRow;
                if (i >= 0 && i < rows.Length)
                {
                    TuiHelpers.OpenInDefaultHandler(rows[i].ResolvedPath);
                }
                key.Handled = true;
            }
        };

        app.Run(window);
    }

    internal static string RenderDetail(InstalledSkill s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {s.Name}");
        sb.AppendLine();
        sb.AppendLine($"**path**: `{s.ResolvedPath}`  ");
        sb.AppendLine($"**scope**: {s.Scope}  ");
        sb.AppendLine($"**provenance**: {s.Provenance}  ");
        sb.AppendLine($"**validity**: {(s.Validity == ValidityState.Valid ? "✅ Valid" : $"⚠️ {s.Validity}")}  ");
        sb.AppendLine($"**symlinked**: {s.IsSymlinked}  ");
        sb.AppendLine($"**pinned**: {s.Pinned}  ");
        sb.AppendLine($"**ignored**: {s.Ignored}  ");
        sb.AppendLine($"**tree-sha**: `{s.TreeSha ?? "(unset)"}`  ");
        sb.AppendLine($"**version**: {s.FrontMatter.Version ?? "(unset)"}  ");
        if (s.InstalledAt is { } when_)
        {
            sb.AppendLine($"**installed**: {when_.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}  ");
        }
        if (s.FrontMatter.Description is { Length: > 0 } desc)
        {
            sb.AppendLine();
            sb.AppendLine("### Description");
            sb.AppendLine();
            sb.AppendLine(desc);
        }
        if (s.Agents.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Agents");
            sb.AppendLine();
            foreach (var a in s.Agents)
            {
                var kind = a.IsSymlink ? "symlink" : "direct";
                sb.AppendLine($"- **{a.AgentId}** ({kind}) `{a.Path}`");
            }
        }
        return sb.ToString();
    }
}
