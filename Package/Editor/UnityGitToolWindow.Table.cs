using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Right panel: Property | A | B, plus a Result column sitting between them (Rider's
    /// convention for a 3-way merge) when there's actually something to resolve (see
    /// <c>BuildTablePanel</c>'s <c>isMerge</c>) — omitted entirely for a plain two-revision diff. A
    /// Take-A/Take-B arrow sits inline in each side's own cell rather than a separate Actions
    /// column.</summary>
    public partial class UnityGitToolWindow
    {
        private enum ColumnSide { A, B, Result }

        /// <summary><paramref name="isMerge"/> is nothing to resolve outside a real conflict (see
        /// UnityGitToolWindow.cs's <c>DetectAndApplyConflictState</c>) — the Result column doesn't
        /// exist at all then, not just hidden, since there's no write-back to preview.
        /// <see cref="_columnResult"/> is left null in that case; every other bit of Result-only UI
        /// (BindHeaderValueCell's delete toggle, the field rows' revert button) is naturally
        /// unreachable once the column itself is never added.</summary>
        private VisualElement BuildTablePanel(bool isMerge)
        {
            var pane = new VisualElement { style = { flexGrow = 1 } };
            pane.Add(BuildPanelTitle(isMerge ? "Diff / Merge" : "Diff"));

            _columnA = BuildColumn("A", 210, r => r.ValueA, ColumnSide.A);
            _columnB = BuildColumn("B", 210, r => r.ValueB, ColumnSide.B);
            _table = new MultiColumnListView
            {
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                // Rows must grow to fit multi-line content (arrays, lists) — the default fixed-height
                // virtualization clips anything taller than one line.
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
            };
            _table.columns.Add(BuildPropertyColumn());
            _table.columns.Add(_columnA);
            if (isMerge)
            {
                _columnResult = BuildColumn("Result", 210, r => r.Result, ColumnSide.Result);
                _table.columns.Add(_columnResult);
            }
            else
            {
                _columnResult = null;
            }
            _table.columns.Add(_columnB);
            _table.AddToClassList("gt-table");
            pane.Add(_table);
            return pane;
        }

        private Column BuildPropertyColumn()
        {
            return new Column
            {
                title = "Property",
                width = 140,
                minWidth = 90,
                stretchable = true,
                makeCell = () =>
                {
                    var cell = new VisualElement();
                    cell.AddToClassList("gt-cell");
                    cell.AddToClassList("gt-property-cell");

                    var typeIcon = new Image { scaleMode = ScaleMode.ScaleToFit };
                    typeIcon.AddToClassList("gt-tree-icon");
                    typeIcon.AddToClassList("gt-icon-sm");
                    typeIcon.AddToClassList("gt-icon-leading");
                    cell.Add(typeIcon);

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
                    var typeIcon = (Image)element[0];
                    var warnIcon = (Image)element[1];
                    var label = (Label)element[2];

                    element.EnableInClassList("gt-row-header", data.IsHeader);
                    if (data.IsHeader)
                    {
                        // Inspector-style section bar: component icon, bold name, no conflict icon (a
                        // header itself is never a mergeable field). The name's own color/weight never
                        // reflects delete state — that lives entirely in the Result column's
                        // trash/restore toggle (see BuildColumn's header handling) so the name stays
                        // readable regardless of what's marked.
                        typeIcon.style.display = DisplayStyle.Flex;
                        typeIcon.image = GetIcon(data.HeaderIcon);
                        warnIcon.style.display = DisplayStyle.None;
                        label.text = data.Property;
                        label.AddToClassList("gt-row-header-label");
                        return;
                    }

                    typeIcon.style.display = DisplayStyle.None;
                    warnIcon.style.display = _isMerge && data.IsConflict ? DisplayStyle.Flex : DisplayStyle.None;
                    label.RemoveFromClassList("gt-row-header-label");
                    label.text = data.Property;
                },
            };
        }

        /// <summary>Wires a header row's trash/restore button (lives in the Result column — see
        /// BuildColumn's header handling, not beside the name) — trash icon while not marked (click
        /// marks it), a "restore" icon once marked (click un-marks it). <paramref name="alsoRefresh"/>
        /// is the Result tree, for a GameObject delete: that tree's own row for the same node (see
        /// UnityGitToolWindow.Tree.cs's BindTreeRow) needs a rebind too, since it shows the same
        /// strike-through independently of this table. <paramref name="inherited"/> is true for a
        /// component header whose owning GameObject header is itself marked for deletion — it still
        /// shows the restore icon (the component IS going away), but the button is disabled and
        /// unwired: it isn't independently actionable, since restoring just the component would do
        /// nothing while the whole GameObject stays marked.</summary>
        private void BindDeleteToggle(Button button, string what, System.Func<bool> getMarked, System.Action<bool> setMarked, TreeView alsoRefresh = null, bool inherited = false)
        {
            var marked = inherited || getMarked();
            button.style.display = DisplayStyle.Flex;
            button.SetEnabled(!inherited);
            button.tooltip = inherited
                ? $"{what} — deleted along with its GameObject"
                : marked ? $"Restore {what} to the result" : $"Delete {what} from the result";
            button.Clear();
            button.Add(new Image { image = GetIcon(marked ? "Refresh" : "TreeEditor.Trash"), scaleMode = ScaleMode.ScaleToFit });

            button.clicked -= button.userData as System.Action; // clear the previous row's handler before rebinding
            if (inherited) { button.userData = null; return; } // nothing to wire — see doc comment above

            System.Action onClick = () =>
            {
                setMarked(!getMarked());
                _table.RefreshItems();
                alsoRefresh?.RefreshItems();
            };
            button.userData = onClick;
            button.clicked += onClick;
        }

        private static Color HeaderStateColor(MockHeaderState state) => state switch
        {
            MockHeaderState.Added => BadgeGreen,
            MockHeaderState.Deleted => BadgeRed,
            MockHeaderState.Modified => BadgeOrange,
            MockHeaderState.Missing => new Color(0.6f, 0.6f, 0.6f),
            _ => Color.white,
        };

        /// <summary>Whether <paramref name="row"/> falls under <paramref name="header"/> — walks
        /// <see cref="MockRow.OwnerHeaderRow"/>'s chain (a component header's own OwnerHeaderRow is the
        /// GameObject header — see that field's doc comment — so this naturally includes a GameObject
        /// header's descendants two levels down: its components' field rows, not just its own).</summary>
        private static bool IsUnderHeader(MockRow row, MockRow header)
        {
            for (var owner = row.OwnerHeaderRow; owner != null; owner = owner.OwnerHeaderRow)
                if (owner == header) return true;
            return false;
        }

        /// <summary>Take-A/Take-B for a header row's arrow (see BuildColumn's header handling) applies
        /// to every row underneath that header at once — its own field rows for a component header, or
        /// every component's plus its own for the GameObject header — rather than needing each field
        /// picked one at a time.</summary>
        private void BulkTake(MockRow header, ColumnSide side)
        {
            foreach (var row in _currentRows)
            {
                if (row.IsHeader || !IsUnderHeader(row, header)) continue;
                row.Result = side == ColumnSide.A ? row.ValueA : row.ValueB;
                row.Resolution = side == ColumnSide.A ? MockResolution.A : MockResolution.B;
            }
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

                    // B's arrow ("<-", takes B into the center Result column) sits on the leading edge
                    // of its own cell, adjacent to Result; A's ("->") sits trailing, also adjacent to
                    // Result — both read as "push this side's value into the middle", Rider's layout.
                    // Result's own trailing button is Revert, back to unresolved.
                    Button sideButton = null;
                    if (side == ColumnSide.B)
                    {
                        sideButton = new Button { text = "<-" }; // ASCII, not a Unicode arrow glyph — guaranteed to render in the editor font
                        sideButton.AddToClassList("gt-action-button");
                        cell.Add(sideButton);
                    }

                    var fieldSlot = new VisualElement { style = { flexGrow = 1 } };
                    cell.Add(fieldSlot);

                    if (side == ColumnSide.A)
                    {
                        sideButton = new Button { text = "->" };
                        sideButton.AddToClassList("gt-action-button");
                        cell.Add(sideButton);
                    }

                    Button revertButton = null;
                    if (isResult)
                    {
                        revertButton = new Button();
                        revertButton.AddToClassList("gt-action-button");
                        revertButton.AddToClassList("gt-icon-button");
                        revertButton.tooltip = "Revert to unresolved";
                        revertButton.Add(new Image { image = GetIcon("Refresh"), scaleMode = ScaleMode.ScaleToFit });
                        cell.Add(revertButton);
                    }
                    return cell;
                },
                bindCell = (element, index) =>
                {
                    var row = (List<MockRow>)_table.itemsSource;
                    var data = row[index];
                    // Matches makeCell's child order above: B's cell is [arrow, fieldSlot], A's and
                    // Result's are both [fieldSlot, button].
                    var fieldSlot = element[side == ColumnSide.B ? 1 : 0];
                    fieldSlot.Clear(); // cells are recycled across rows — previous row's field type may differ
                    element.EnableInClassList("gt-row-header", data.IsHeader);

                    if (data.IsHeader)
                    {
                        BindHeaderValueCell(element, data, side, fieldSlot);
                        return;
                    }

                    // A field whose whole owner (component or GameObject) is marked for deletion never
                    // actually gets written — UnityYamlWriter skips it, the owner's whole document is
                    // removed instead (see MockRow.OwnerHeaderRow) — so there's nothing left to take
                    // A/B into, or to revert. Only ever true in Merge mode — outside it there's no
                    // Result column to mark a delete from in the first place.
                    var ownerDeleted = data.OwnerHeaderRow?.IsMarkedForDeletion == true;

                    // Every cell also carries the A/B take-arrow (or Result's revert) as a sibling of
                    // the field slot — start hidden and let each branch below decide whether it has
                    // something to wire. Outside Merge mode the take-arrow itself means nothing (no
                    // Result to take into), but B's slot is repurposed as a Rollback button when B is
                    // Working Tree (see the plain-value branch below) — the one action that still makes
                    // sense in a bare two-revision diff: discard an uncommitted change straight from
                    // the file on disk.
                    foreach (var button in element.Query<Button>().ToList())
                        button.style.display = DisplayStyle.None;

                    if (isResult && ownerDeleted)
                    {
                        // Deliberately not "whatever Result happened to hold" — that value is about to
                        // be discarded along with the rest of its owner, and showing it as if it were
                        // still the outcome would be misleading.
                        fieldSlot.Add(new Label("(deleted)") { style = { opacity = 0.5f, unityFontStyleAndWeight = FontStyle.Italic } });
                        return;
                    }

                    if (isResult)
                    {
                        // An unresolved conflict is seeded from whichever side has a value, so typing
                        // directly is possible right away instead of requiring a Take A/B click first.
                        // A non-conflict row's Result is already the real auto-merge outcome — a null
                        // there (e.g. a clean deletion) means the field genuinely doesn't exist
                        // anymore, so it must NOT fall back to A/B like the conflict case does.
                        var seed = data.IsConflict ? data.Result ?? data.ValueA ?? data.ValueB : data.Result;
                        // A direct edit is a real value but not one UnityYamlWriter can safely
                        // re-serialize back into YAML (see MockResolution.Manual) — excluded from
                        // write-back and reported rather than guessed at.
                        var field = FieldFactory.Create(seed, enabled: true, onValueChanged: v =>
                        {
                            data.Result = v;
                            data.Resolution = MockResolution.Manual;
                        });
                        fieldSlot.Add(field);

                        var revertButton = element.Q<Button>(className: "gt-icon-button");
                        if (revertButton != null)
                        {
                            revertButton.style.display = DisplayStyle.Flex;
                            revertButton.clicked -= revertButton.userData as System.Action;
                            System.Action onRevert = () => { data.Result = null; data.Resolution = MockResolution.Unresolved; _table.RefreshItems(); };
                            revertButton.userData = onRevert;
                            revertButton.clicked += onRevert;
                        }
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
                        fieldSlot.Add(field);

                        // Whole-value color first (green/red/orange for added/removed/modified),
                        // then narrow it down to just the differing axis for a Vector2/3 — any
                        // unchanged axis gets reset to no color by that second pass.
                        var statusColor = GetStatusColor(data, side);
                        FieldFactory.SetValueColor(field, statusColor);
                        FieldFactory.HighlightChangedComponents(field, thisValue, otherValue, statusColor ?? BadgeOrange);

                        var actionButton = element.Q<Button>(className: "gt-action-button");
                        if (actionButton == null) { }
                        else if (_isMerge)
                        {
                            // Text arrow, Rider-style — restore its text form in case this same cell
                            // was last bound as a Rollback icon button (cells are recycled across rows).
                            actionButton.style.display = DisplayStyle.Flex;
                            actionButton.Clear();
                            actionButton.text = side == ColumnSide.A ? "->" : "<-";
                            actionButton.tooltip = thisValue != null ? $"Take {title}'s value" : $"Take {title}'s value (doesn't exist — resolves to deleted)";
                            actionButton.clicked -= actionButton.userData as System.Action;
                            System.Action onTake = () =>
                            {
                                data.Result = thisValue;
                                data.Resolution = side == ColumnSide.A ? MockResolution.A : MockResolution.B;
                                _table.RefreshItems();
                            };
                            actionButton.userData = onTake;
                            actionButton.clicked += onTake;
                        }
                        else if (side == ColumnSide.B && isWorkingTree && data.OwnerHeaderRow?.HeaderStateB != MockHeaderState.Deleted)
                        {
                            // Outside Merge mode there's no Result to take a value into — but B being
                            // Working Tree means this field is a real, uncommitted, writable change, so
                            // the one action still worth offering is discarding it: roll this one field
                            // back to A's value, written straight to the file on disk (see
                            // UnityGitToolWindow.Apply.cs's RollbackField). Icon rather than the
                            // text arrow — this isn't "take a side into a result", it's "undo". Gated on
                            // the owner NOT being wholly missing from the working tree (HeaderStateB ==
                            // Deleted) — a field on a component/GameObject that doesn't exist there at
                            // all has nothing real to roll back; that's the header's own bulk Rollback
                            // button instead (see BindHeaderValueCell / RollbackHeader), which restores
                            // the whole missing document rather than pretending one field can stand in
                            // for it.
                            actionButton.style.display = DisplayStyle.Flex;
                            actionButton.text = "";
                            actionButton.Clear();
                            actionButton.Add(new Image { image = GetIcon("Refresh"), scaleMode = ScaleMode.ScaleToFit });
                            actionButton.tooltip = $"Rollback to {_revisionA}'s value (discards this uncommitted change)";
                            actionButton.clicked -= actionButton.userData as System.Action;
                            System.Action onRollback = () => RollbackField(data);
                            actionButton.userData = onRollback;
                            actionButton.clicked += onRollback;
                        }
                    }
                },
            };
        }

        /// <summary>Header row (component or GameObject) binding for the A/B/Result columns, laid out
        /// like a value row so the two read consistently. A/B's diff-status text (see
        /// <see cref="MockHeaderState"/>) shows regardless of mode — it's the same read-only diff
        /// info the Files tree's A/M/D chip carries, not a merge action — but the bulk take-arrow next
        /// to it (applies every field under this header to that side at once, see
        /// <see cref="BulkTake"/>) only does anything once there's a Result to take into, so it's
        /// Merge-only. The Result branch below is only ever reached in Merge mode in practice — the
        /// column itself doesn't exist otherwise (see <c>BuildTablePanel</c>'s <c>isMerge</c>) — and
        /// carries this header's own delete/restore toggle (moved here from the Property column, next
        /// to the value columns' other actions rather than beside the name — see
        /// <see cref="BindDeleteToggle"/>).</summary>
        private void BindHeaderValueCell(VisualElement element, MockRow data, ColumnSide side, VisualElement fieldSlot)
        {
            foreach (var button in element.Query<Button>().ToList())
                button.style.display = DisplayStyle.None;

            if (side == ColumnSide.Result)
            {
                var deleteButton = element.Q<Button>(className: "gt-icon-button");
                if (deleteButton == null) return;
                if (data.GameObjectNode != null)
                {
                    BindDeleteToggle(deleteButton, "this GameObject (and its children)",
                        () => data.GameObjectNode.MarkedForDeletion, v => data.GameObjectNode.MarkedForDeletion = v,
                        alsoRefresh: _resultTree);
                }
                else if (data.FileId != 0) // 0 = component only exists on B — write-back only ever patches A, nothing there yet to delete
                {
                    var inherited = data.OwnerHeaderRow?.IsMarkedForDeletion == true;
                    BindDeleteToggle(deleteButton, "this component",
                        () => data.MarkedForDeletion, v => data.MarkedForDeletion = v, inherited: inherited);
                }
                return;
            }

            var state = side == ColumnSide.A ? data.HeaderStateA : data.HeaderStateB;
            if (state == MockHeaderState.None) return;

            // Added/Deleted/Missing are asymmetric — each side genuinely reads differently, worth
            // stating on both. Modified isn't: both sides equally "have a value that differs from the
            // other's", so the same word on both columns says nothing new the second time. Shown once,
            // on B — A still gets its take-arrow below regardless, since taking from A is just as
            // meaningful as taking from B when both sides have a value.
            if (state != MockHeaderState.Modified || side == ColumnSide.B)
            {
                fieldSlot.Add(new Label(state.ToString())
                {
                    style = { color = HeaderStateColor(state), unityFontStyleAndWeight = FontStyle.Bold },
                });
            }
            if (state == MockHeaderState.Missing) return; // nothing on this side to take or roll back

            var what = data.GameObjectNode != null ? "GameObject" : "component";
            var actionButton = element.Q<Button>(className: "gt-action-button");
            if (actionButton == null) return;

            if (_isMerge)
            {
                actionButton.style.display = DisplayStyle.Flex;
                actionButton.tooltip = $"Take every field in this {what} from {(side == ColumnSide.A ? "A" : "B")}";
                actionButton.clicked -= actionButton.userData as System.Action;
                System.Action onClick = () => { BulkTake(data, side); _table.RefreshItems(); };
                actionButton.userData = onClick;
                actionButton.clicked += onClick;
                return;
            }

            // Outside Merge mode: a bulk Rollback button on B, same idea as a single field's (see
            // BuildColumn's plain-value branch) but for this whole component/GameObject at once — the
            // header equivalent of "discard every uncommitted change under here". Two very different
            // operations share this one button depending on whether B still has the object at all:
            // Modified rolls back every field row under this header (see RollbackHeader); Deleted
            // restores the whole missing document (and, for a component, its owning GameObject too if
            // that's ALSO missing) rather than pretending there are fields to splice into something
            // that doesn't exist in the working tree yet.
            var isWorkingTree = side == ColumnSide.A ? _revisionA == WorkingTree : _revisionB == WorkingTree;
            if (side != ColumnSide.B || !isWorkingTree || state == MockHeaderState.Added) return;

            actionButton.style.display = DisplayStyle.Flex;
            actionButton.text = ""; // this same recycled button may have last shown a merge-mode text arrow
            actionButton.Clear();
            actionButton.Add(new Image { image = GetIcon("Refresh"), scaleMode = ScaleMode.ScaleToFit });
            actionButton.tooltip = state == MockHeaderState.Deleted
                ? $"Restore this {what} from {_revisionA} (it doesn't exist in the working tree)"
                : $"Rollback every field in this {what} to {_revisionA}'s value";
            actionButton.clicked -= actionButton.userData as System.Action;
            System.Action onRollback = () => RollbackHeader(data);
            actionButton.userData = onRollback;
            actionButton.clicked += onRollback;
        }

        /// <summary>Status color for a value cell's text: green when this side introduces the field
        /// (the other side is null, i.e. added), red when this side loses it (removed), orange when
        /// both sides have a value but differ (modified), or red when they differ AND the row is
        /// still an unresolved conflict. Never shown on the Result column. <see cref="MockRow.BIsOlderBaseline"/>
        /// flips which side counts as "introduces"/"loses" the field: normally A is the reference/older
        /// side and B is the one introducing a change (so missing-from-A reads as added), but for a
        /// non-conflicted file's HEAD fallback (see UnityGitToolWindow.Tree.cs) A is the newer side and
        /// B the older baseline it's compared against, so it's missing-from-B that reads as added.</summary>
        private static Color? GetStatusColor(MockRow row, ColumnSide side)
        {
            if (side == ColumnSide.Result) return null;

            var addedSide = row.BIsOlderBaseline ? ColumnSide.A : ColumnSide.B;
            var removedSide = row.BIsOlderBaseline ? ColumnSide.B : ColumnSide.A;
            var added = row.BIsOlderBaseline ? row.ValueB == null : row.ValueA == null;
            var removed = row.BIsOlderBaseline ? row.ValueA == null : row.ValueB == null;
            if (added) return side == addedSide ? BadgeGreen : null;
            if (removed) return side == removedSide ? BadgeRed : null;

            var unresolved = row.IsConflict && row.Result == null;
            return unresolved ? BadgeRed : BadgeOrange; // both sides have a (different) value
        }
    }
}
