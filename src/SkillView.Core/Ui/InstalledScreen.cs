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
    private enum SortMode { Name, Package, Scope }
    internal enum ShortcutCommand { None, Close, GoToSearch, FocusFilter, FocusTable, CycleSort, Remove, Open }
    internal readonly record struct ShortcutDecision(ShortcutCommand Command, bool RequestStop);

    public static void Show(
        IApplication app,
        InventorySnapshot snapshot,
        Action<InstalledSkill>? onRemove = null,
        Action? onGoToSearch = null)
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

        var filterLabel = new Label { Text = "Filter:", X = 0, Y = 0 };
        var filterField = new TextField
        {
            X = 8, Y = 0,
            Width = Dim.Percent(60) - 8,
            Text = string.Empty,
        };
        TuiHelpers.ConfigureTextInput(filterField, "Base");

        var table = new TableView
        {
            X = 0,
            Y = 2,
            Width = Dim.Percent(60),
            Height = Dim.Fill(2),
            FullRowSelect = true,
        };
        TuiHelpers.DisableTypeToSearch(table);
        var allRows = snapshot.Skills;
        IReadOnlyList<InstalledSkill> rows = allRows;

        // How many distinct packages are present — used by the footer hint
        // and to decide whether the Package column is worth showing.
        var packageCount = allRows
            .Where(s => s.Package is not null)
            .Select(s => s.Package!.Source)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var hasPackages = packageCount > 0;

        var sort = hasPackages ? SortMode.Package : SortMode.Name;

        IReadOnlyList<InstalledSkill> ApplySort(IEnumerable<InstalledSkill> input) => sort switch
        {
            SortMode.Package => input
                .OrderBy(s => s.Package?.Source ?? "~", StringComparer.OrdinalIgnoreCase) // unpackaged sorts last
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SortMode.Scope => input
                .OrderBy(s => s.Scope)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => input
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };

        void ApplyFilter()
        {
            var q = filterField.Text.Trim();
            IEnumerable<InstalledSkill> source = allRows;
            if (q.Length > 0)
            {
                var cmp = StringComparison.OrdinalIgnoreCase;
                source = allRows.Where(s =>
                    s.Name.Contains(q, cmp)
                    || s.ResolvedPath.Contains(q, cmp)
                    || s.Agents.Any(a => a.AgentId.Contains(q, cmp))
                    || (s.Package?.Source.Contains(q, cmp) ?? false));
            }
            rows = ApplySort(source);
        }

        // Initial sort.
        rows = ApplySort(allRows);

        var nameW = 12;
        var pkgW = 18;
        var agentsW = 8;

        // Build the source once; lambdas read from the mutable widths.
        // Conditionally include the Package column — when no skills are
        // packaged the column would be 100% blank and waste horizontal real
        // estate. The lockfile reader still runs; this is purely display.
        var columns = new Dictionary<string, Func<InstalledSkill, object>>
        {
            ["Name"] = s => TuiHelpers.Truncate(s.Name, nameW),
            ["Scope"] = s => DisplayScope(s.Scope),
        };
        if (hasPackages)
        {
            columns["Package"] = s => TuiHelpers.Truncate(s.Package?.Source ?? "", pkgW);
        }
        columns["!"] = s => s.Validity == ValidityState.Valid ? "" : "!";
        columns["Lnk"] = s => s.IsSymlinked ? "↩" : "";
        columns["Agents"] = s => TuiHelpers.Truncate(
            TuiHelpers.AgentBadges(s.Agents.Select(a => a.AgentId)),
            agentsW);

        // EnumerableTableSource closes over the `rows` *variable*, but the
        // wrapper / inner-source refresh happens by reassigning table.Table
        // when filter or sort changes (cheap — just a new wrapper view over a
        // pre-computed list).
        InstalledTableSource? currentSource = null;

        void BuildSource()
        {
            currentSource = new InstalledTableSource(rows, columns);
            table.Table = currentSource;
            var style = table.Style;
            style.ExpandLastColumn = true;
            for (var i = 0; i < table.Table.Columns; i++)
            {
                var cs = style.GetOrCreateColumnStyle(i);
                switch (table.Table.ColumnNames[i])
                {
                    case "Name": cs.MinWidth = 8; cs.MaxWidth = nameW; break;
                    case "Package": cs.MinWidth = 8; cs.MaxWidth = pkgW; break;
                    case "Agents": cs.MinWidth = 6; break;
                }
            }
            table.Update();
        }

        void Recompute()
        {
            var viewportWidth = table.Viewport.Width;
            var available = viewportWidth > 0
                ? Math.Max(40, viewportWidth - 6 /* col separators */)
                : 70;
            // Scope/!/Lnk are short fixed-width. Package + Name + Agents share
            // the remainder. When Package isn't present, give Name more room.
            var fixedCols = 6 /*Scope*/ + 1 /*!*/ + 3 /*Lnk*/;
            var remaining = Math.Max(20, available - fixedCols);
            if (hasPackages)
            {
                pkgW = Math.Max(12, (int)Math.Round(remaining * 0.30));
                nameW = Math.Max(12, (int)Math.Round(remaining * 0.40));
                agentsW = Math.Max(6, remaining - pkgW - nameW);
            }
            else
            {
                nameW = Math.Max(12, (int)Math.Round(remaining * 0.65));
                agentsW = Math.Max(6, remaining - nameW);
            }
            // Column-style refs are stable; just update widths and redraw.
            var style = table.Style;
            for (var i = 0; i < (table.Table?.Columns ?? 0); i++)
            {
                var name = table.Table!.ColumnNames[i];
                var cs = style.GetOrCreateColumnStyle(i);
                if (name == "Name") cs.MaxWidth = nameW;
                else if (name == "Package") cs.MaxWidth = pkgW;
            }
            table.SetNeedsDraw();
        }

        BuildSource();
        Recompute();
        var lastWidth = -1;
        table.FrameChanged += (_, _) =>
        {
            var w = table.Viewport.Width;
            if (w > 0 && w != lastWidth)
            {
                lastWidth = w;
                Recompute();
            }
        };

        var detail = new Markdown
        {
            X = Pos.Right(table),
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Text = rows.Count == 0 ? "(no skills found)" : RenderDetail(rows[0]),
        };
        TuiHelpers.ConfigureMarkdownPane(detail, "Base");

        table.ValueChanged += (_, _) =>
        {
            var row = table.GetSelectedRow();
            if (row >= 0 && row < rows.Count)
            {
                detail.Text = RenderDetail(rows[row]);
            }
        };

        var footer = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Text = BuildFooter(rows.Count, allRows.Length, snapshot, sort, packageCount),
        };

        void RefreshFooter() =>
            footer.Text = BuildFooter(rows.Count, allRows.Length, snapshot, sort, packageCount);

        void RefreshAll()
        {
            ApplyFilter();
            BuildSource();
            Recompute();
            RefreshFooter();
            detail.Text = rows.Count == 0
                ? "(no matches)"
                : RenderDetail(rows[Math.Clamp(table.GetSelectedRow(), 0, rows.Count - 1)]);
        }

        filterField.TextChanged += (_, _) => RefreshAll();

        var statusBar = new StatusBar(BuildShortcuts(onRemove is not null, hasPackages));

        TuiHelpers.ApplyScheme("Base", window, filterLabel, filterField, table, detail, footer, statusBar);

        window.Add(filterLabel, filterField, table, detail, footer, statusBar);
        var goToSearchRequested = false;

        // Single dispatcher used by both window.KeyDown and table.KeyDown.
        // Returns true if the key was handled. We do NOT re-inject keys into
        // the view hierarchy via NewKeyDownEvent — that re-enters Terminal.Gui's
        // dispatch pipeline and crashes when the action (e.g. q → RequestStop)
        // tears down the run loop mid-call.
        bool HandleShortcut(Key key)
        {
            var decision = DecideShortcut(key, filterField.HasFocus, onRemove is not null);
            if (decision.Command == ShortcutCommand.None)
            {
                return false;
            }

            if (decision.RequestStop)
            {
                app.RequestStop();
            }

            switch (decision.Command)
            {
                case ShortcutCommand.GoToSearch:
                    goToSearchRequested = true;
                    break;
                case ShortcutCommand.FocusFilter:
                    filterField.SetFocus();
                    filterField.SelectAll();
                    break;
                case ShortcutCommand.FocusTable:
                    table.SetFocus();
                    break;
                case ShortcutCommand.CycleSort:
                    sort = sort switch
                    {
                        SortMode.Name => hasPackages ? SortMode.Package : SortMode.Scope,
                        SortMode.Package => SortMode.Scope,
                        _ => SortMode.Name,
                    };
                    RefreshAll();
                    break;
                case ShortcutCommand.Remove:
                {
                    var i = table.GetSelectedRow();
                    if (i >= 0 && i < rows.Count) onRemove?.Invoke(rows[i]);
                    break;
                }
                case ShortcutCommand.Open:
                {
                    var i = table.GetSelectedRow();
                    if (i >= 0 && i < rows.Count)
                    {
                        TuiHelpers.OpenInDefaultHandler(rows[i].ResolvedPath);
                    }
                    break;
                }
            }

            return true;
        }

        window.KeyDown += (_, key) =>
        {
            if (HandleShortcut(key)) key.Handled = true;
        };

        // RC5: TableView swallows unbound printable letters in
        // OnKeyDownNotHandled. Dispatch shortcuts directly here — the table's
        // KeyDown event fires before OnKeyDownNotHandled.
        table.KeyDown += (_, key) =>
        {
            if (HandleShortcut(key)) key.Handled = true;
        };

        table.SetFocus();
        app.Run(window);
        if (goToSearchRequested)
        {
            onGoToSearch?.Invoke();
        }
    }

    internal static Shortcut[] BuildShortcuts(bool canRemove, bool hasPackages)
    {
        var list = new List<Shortcut>
        {
            new() { Key = (Key)'/', Title = "/", HelpText = "Search" },
            new() { Key = (Key)'f', Title = "f", HelpText = "Filter" },
        };
        if (canRemove) list.Add(new() { Key = (Key)'x', Title = "x", HelpText = "Remove" });
        list.Add(new() { Key = (Key)'o', Title = "o", HelpText = "Open" });
        if (hasPackages) list.Add(new() { Key = (Key)'s', Title = "s", HelpText = "Sort" });
        list.Add(new() { Key = Key.Esc, Title = "Esc", HelpText = "Back" });
        list.Add(new() { Key = (Key)'q', Title = "q", HelpText = "Quit" });
        return list.ToArray();
    }

    internal static ShortcutDecision DecideShortcut(Key key, bool filterHasFocus, bool canRemove)
    {
        if (filterHasFocus)
        {
            if (key.AsRune.Value == '/')
            {
                return new ShortcutDecision(ShortcutCommand.GoToSearch, RequestStop: true);
            }

            return key.KeyCode == KeyCode.Esc
                ? new ShortcutDecision(ShortcutCommand.FocusTable, RequestStop: false)
                : default;
        }

        if (key.KeyCode == KeyCode.Esc || key.AsRune.Value == 'q' || key.AsRune.Value == 'Q')
        {
            return new ShortcutDecision(ShortcutCommand.Close, RequestStop: true);
        }

        var r = key.AsRune.Value;
        if (r == '/')
        {
            return new ShortcutDecision(ShortcutCommand.GoToSearch, RequestStop: true);
        }
        if (r == 'f' || r == 'F')
        {
            return new ShortcutDecision(ShortcutCommand.FocusFilter, RequestStop: false);
        }
        if (r == 's' || r == 'S')
        {
            return new ShortcutDecision(ShortcutCommand.CycleSort, RequestStop: false);
        }
        if (canRemove && (r == 'x' || r == 'X'))
        {
            return new ShortcutDecision(ShortcutCommand.Remove, RequestStop: false);
        }
        if (r == 'o' || r == 'O')
        {
            return new ShortcutDecision(ShortcutCommand.Open, RequestStop: false);
        }

        return default;
    }

    private static string DisplayScope(Scope s) => s switch
    {
        Scope.User => "Global",
        _ => s.ToString(),
    };

    private static string SortLabel(SortMode m) => m switch
    {
        SortMode.Package => "package",
        SortMode.Scope => "scope",
        _ => "name",
    };

    private static string BuildFooter(int shown, int total, InventorySnapshot snapshot, SortMode sort, int packageCount)
    {
        var counts = shown == total
            ? $" {total} skill(s) across {snapshot.ScannedRoots.Length} root(s)"
            : $" {shown} of {total} skill(s) (filtered) · {snapshot.ScannedRoots.Length} root(s)";
        var pkgs = packageCount > 0 ? $" · {packageCount} package(s)" : "";
        var srcSuffix = snapshot.UsedGhSkillList ? " · gh data + scan" : " · scan only";
        return $"{counts}{pkgs} · sort: {SortLabel(sort)}{srcSuffix}";
    }

    internal static string RenderDetail(InstalledSkill s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {s.Name}");
        sb.AppendLine();
        sb.AppendLine($"**path**: `{s.ResolvedPath}`  ");
        sb.AppendLine($"**scope**: {DisplayScope(s.Scope)}  ");
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
        if (s.Package is { } pkg)
        {
            sb.AppendLine();
            sb.AppendLine("### 📦 Package");
            sb.AppendLine();
            sb.AppendLine($"**source**: `{pkg.Source}`  ");
            sb.AppendLine($"**type**: {pkg.SourceType}  ");
            if (pkg.SourceUrl is { Length: > 0 } url) sb.AppendLine($"**url**: {url}  ");
            if (pkg.UpdatedAt is { } u)
            {
                sb.AppendLine($"**updated**: {u.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}  ");
            }
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
                sb.AppendLine($"- {TuiHelpers.AgentIcon(a.AgentId)} **{a.AgentId}** ({kind}) `{a.Path}`");
            }
        }
        return sb.ToString();
    }

    /// Tiny `EnumerableTableSource<InstalledSkill>` shim — exists only so we
    /// can keep a stable named type for the `currentSource` field reassignment
    /// pattern (avoids capturing inferred-anonymous generic locals).
    private sealed class InstalledTableSource : EnumerableTableSource<InstalledSkill>
    {
        public InstalledTableSource(IReadOnlyList<InstalledSkill> rows, Dictionary<string, Func<InstalledSkill, object>> cols)
            : base(rows, cols) { }
    }
}
