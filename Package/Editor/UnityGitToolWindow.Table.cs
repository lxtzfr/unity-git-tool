using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Right panel: the Property | A | B | Result | Actions diff/merge table.</summary>
    public partial class UnityGitToolWindow
    {
        private enum ColumnSide { A, B, Result }

        private VisualElement BuildTablePanel()
        {
            var pane = new VisualElement { style = { flexGrow = 1 } };
            pane.Add(BuildPanelTitle("Diff / Merge"));

            _columnA = BuildColumn("A", 210, r => r.ValueA, ColumnSide.A);
            _columnB = BuildColumn("B", 210, r => r.ValueB, ColumnSide.B);
            _columnResult = BuildColumn("Result", 210, r => r.Result, ColumnSide.Result);
            _columnActions = BuildActionsColumn();
            _table = new MultiColumnListView
            {
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                // Rows must grow to fit multi-line content (arrays, lists) — the default fixed-height
                // virtualization clips anything taller than one line.
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                columns =
                {
                    BuildPropertyColumn(),
                    _columnA,
                    _columnB,
                    _columnResult,
                    _columnActions,
                },
            };
            _table.AddToClassList("gt-table");
            pane.Add(_table);
            return pane;
        }

        private Column BuildPropertyColumn()
        {
            return new Column
            {
                title = "Property",
                width = 220,
                minWidth = 140,
                stretchable = true,
                makeCell = () =>
                {
                    var cell = new VisualElement();
                    cell.AddToClassList("gt-cell");
                    cell.AddToClassList("gt-property-cell");

                    var warnIcon = new Image { scaleMode = ScaleMode.ScaleToFit, image = GetIcon("console.warnicon") };
                    warnIcon.AddToClassList("gt-tree-icon");
                    warnIcon.AddToClassList("gt-icon-sm");
                    warnIcon.AddToClassList("gt-icon-leading");
                    cell.Add(warnIcon);

                    cell.Add(new Label());
                    return cell;
                },
                bindCell = (element, index) =>
                {
                    var row = (List<MockRow>)_table.itemsSource;
                    var data = row[index];
                    var warnIcon = (Image)element[0];
                    var label = (Label)element[1];
                    warnIcon.style.display = data.IsConflict ? DisplayStyle.Flex : DisplayStyle.None;
                    label.text = data.Property;
                },
            };
        }

        private Column BuildColumn(string title, int width, System.Func<MockRow, object> getValue, ColumnSide side)
        {
            var isResult = side == ColumnSide.Result;
            return new Column
            {
                title = title,
                width = width,
                minWidth = 160,
                stretchable = true,
                makeCell = () =>
                {
                    var cell = new VisualElement();
                    cell.AddToClassList("gt-cell");
                    cell.AddToClassList("gt-value-cell");
                    if (isResult) cell.AddToClassList("gt-cell-result");
                    return cell;
                },
                bindCell = (element, index) =>
                {
                    var row = (List<MockRow>)_table.itemsSource;
                    var data = row[index];
                    element.Clear(); // cells are recycled across rows — previous row's field type may differ

                    if (isResult)
                    {
                        // An unresolved conflict is seeded from whichever side has a value, so typing
                        // directly is possible right away instead of requiring a Take A/B click first.
                        // A non-conflict row's Result is already the real auto-merge outcome — a null
                        // there (e.g. a clean deletion) means the field genuinely doesn't exist
                        // anymore, so it must NOT fall back to A/B like the conflict case does.
                        var seed = data.IsConflict ? data.Result ?? data.ValueA ?? data.ValueB : data.Result;
                        var field = FieldFactory.Create(seed, enabled: true, onValueChanged: v => data.Result = v);
                        element.Add(field);
                    }
                    else
                    {
                        var thisValue = getValue(data);
                        var otherValue = side == ColumnSide.A ? data.ValueB : data.ValueA;

                        // Only the working-tree side is a real mutable file — a commit is frozen
                        // history, so it stays read-only regardless of which side (A or B) it's on.
                        var isWorkingTree = side == ColumnSide.A ? _revisionA == WorkingTree : _revisionB == WorkingTree;
                        System.Action<object> onValueChanged = isWorkingTree
                            ? v => { if (side == ColumnSide.A) data.ValueA = v; else data.ValueB = v; }
                            : null;
                        var field = FieldFactory.Create(thisValue, enabled: isWorkingTree, onValueChanged: onValueChanged);
                        element.Add(field);

                        // Whole-value color first (green/red/orange for added/removed/modified),
                        // then narrow it down to just the differing axis for a Vector2/3 — any
                        // unchanged axis gets reset to no color by that second pass.
                        var statusColor = GetStatusColor(data, side);
                        FieldFactory.SetValueColor(field, statusColor);
                        FieldFactory.HighlightChangedComponents(field, thisValue, otherValue, statusColor ?? BadgeOrange);
                    }
                },
            };
        }

        /// <summary>Take A / Take B copy that side's value into Result; Revert clears it back to
        /// unresolved. All three just mutate the <see cref="MockRow"/> and ask the table to re-bind
        /// visible cells — safe here since these are discrete clicks, not per-keystroke edits.</summary>
        private Column BuildActionsColumn()
        {
            return new Column
            {
                title = "",
                width = 108,
                stretchable = false,
                makeCell = () =>
                {
                    var cell = new VisualElement();
                    cell.AddToClassList("gt-cell");
                    cell.AddToClassList("gt-actions-cell");
                    return cell;
                },
                bindCell = (element, index) =>
                {
                    var row = (List<MockRow>)_table.itemsSource;
                    var data = row[index];
                    element.Clear();

                    var takeA = new Button(() => { data.Result = data.ValueA; _table.RefreshItems(); }) { text = "A" };
                    takeA.AddToClassList("gt-action-button");
                    takeA.tooltip = data.ValueA != null ? "Take A's value" : "Take A's value (doesn't exist — resolves to deleted)";
                    element.Add(takeA);

                    var takeB = new Button(() => { data.Result = data.ValueB; _table.RefreshItems(); }) { text = "B" };
                    takeB.AddToClassList("gt-action-button");
                    takeB.tooltip = data.ValueB != null ? "Take B's value" : "Take B's value (doesn't exist — resolves to deleted)";
                    element.Add(takeB);

                    var revert = new Button(() => { data.Result = null; _table.RefreshItems(); });
                    revert.AddToClassList("gt-action-button");
                    revert.AddToClassList("gt-icon-button");
                    revert.style.marginRight = 0; // last button — trailing space comes from the cell's own padding instead
                    revert.tooltip = "Revert to unresolved";
                    revert.Add(new Image { image = GetIcon("Refresh"), scaleMode = ScaleMode.ScaleToFit });
                    element.Add(revert);
                },
            };
        }

        /// <summary>Status color for a value cell's text: green when this side introduces the field
        /// (the other side is null, i.e. added), red when this side loses it (removed), orange when
        /// both sides have a value but differ (modified), or red when they differ AND the row is
        /// still an unresolved conflict. Never shown on the Result column.</summary>
        private static Color? GetStatusColor(MockRow row, ColumnSide side)
        {
            if (side == ColumnSide.Result) return null;

            var added = row.ValueA == null;
            var removed = row.ValueB == null;
            if (added) return side == ColumnSide.B ? BadgeGreen : null;
            if (removed) return side == ColumnSide.A ? BadgeRed : null;

            var unresolved = row.IsConflict && row.Result == null;
            return unresolved ? BadgeRed : BadgeOrange; // both sides have a (different) value
        }
    }
}
