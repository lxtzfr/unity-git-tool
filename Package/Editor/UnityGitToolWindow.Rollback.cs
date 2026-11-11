using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Diff mode with B = Working Tree: a Rollback click only marks a field / component /
    /// GameObject; the toolbar's "Rollback (N)" writes every marked change back to A's value, one write
    /// and one import per file. Writing on each click reimported the file (and reloaded an open scene)
    /// every time, and rebuilt the whole tree.</summary>
    public partial class UnityGitToolWindow
    {
        private Button _rollbackButton;

        private bool CanRollback => !_isMerge && _revisionB == WorkingTree;

        /// <summary>A field row that has a working-tree value to put back: its owner exists in the
        /// working tree (a missing owner is restored whole from its header instead) and the writer can
        /// locate it (a FileId and a raw key).</summary>
        private static bool CanRollbackField(MockRow row) =>
            !row.IsHeader && row.FileId != 0 && row.Key != null && row.OwnerHeaderRow?.HeaderStateB != MockHeaderState.Deleted;

        /// <summary>Marked changes under a tree node (itself included): field rows, restored components
        /// and restored GameObjects.</summary>
        private static int CountRollbacks(int nodeId) =>
            (MockDataSource.RowsByNodeId.GetValueOrDefault(nodeId)?.Count(r => r.MarkedForRollback) ?? 0) +
            (MockDataSource.GameObjectNodesByNodeId.GetValueOrDefault(nodeId)?.Count(n => n.MarkedForRollback) ?? 0);

        private int CountPendingRollbacks() => _roots.Sum(root => CountRollbacks(root.id));

        private VisualElement BuildRollbackButton()
        {
            _rollbackButton = new Button(ApplyRollbacks);
            _rollbackButton.AddToClassList("gt-pill");
            _rollbackButton.tooltip = $"Write every marked change back to {_revisionA}'s value";
            UpdatePendingRollbacks();
            return _rollbackButton;
        }

        /// <summary>Refreshes everything that reads the marks: toolbar button, tree counters, and the
        /// window's unsaved-changes state (Unity then asks Save / Discard on close).</summary>
        private void UpdatePendingRollbacks()
        {
            var count = CountPendingRollbacks();
            hasUnsavedChanges = count > 0;
            saveChangesMessage = $"{count} marked rollback(s) not applied yet. Apply them?";
            if (_rollbackButton != null)
            {
                _rollbackButton.style.display = CanRollback ? DisplayStyle.Flex : DisplayStyle.None;
                _rollbackButton.text = count > 0 ? $"Rollback ({count})" : "Rollback";
                _rollbackButton.SetEnabled(count > 0);
                _rollbackButton.tooltip = $"Write every marked change back to {_revisionA}'s value";
            }
            _tree?.RefreshItems();
        }

        public override void SaveChanges()
        {
            ApplyRollbacks();
            base.SaveChanges();
        }

        /// <summary>Before an action that rebuilds every row (revision change, Refresh) and so drops the
        /// marks: Apply / Discard / Cancel. False = the caller must not proceed.</summary>
        private bool ConfirmPendingRollbacks(string action)
        {
            var count = CountPendingRollbacks();
            if (count == 0) return true;
            var choice = EditorUtility.DisplayDialogComplex("Unity Git Tool",
                $"{count} marked rollback(s) not applied yet. {action} drops them.", "Apply them", "Cancel", "Discard");
            if (choice == 1) return false;
            if (choice == 0) ApplyRollbacks();
            return true;
        }

        private void ToggleFieldRollback(MockRow row)
        {
            row.MarkedForRollback = !row.MarkedForRollback;
            _table.RefreshItems();
            UpdatePendingRollbacks();
        }

        /// <summary>The whole document goes back to A: restored when the working tree lacks it, deleted
        /// when only the working tree has it.</summary>
        private static bool IsWholeDocumentState(MockHeaderState state) => state is MockHeaderState.Deleted or MockHeaderState.Added;

        private static bool IsWholeGameObjectRollback(MockRow header) =>
            header.GameObjectNode != null && IsWholeDocumentState(header.GameObjectNode.HeaderStateB);

        private static bool IsWholeComponentRollback(MockRow header) =>
            header.HeaderStateB == MockHeaderState.Deleted ? header.FileId != 0
            : header.HeaderStateB == MockHeaderState.Added && header.GameObjectNode == null && header.DocumentIdB != 0;

        /// <summary>A header present on both sides marks / unmarks every rollbackable field under it
        /// (all marked -> unmark all); one on a single side marks the whole document (restore or delete).</summary>
        private void ToggleHeaderRollback(MockRow header)
        {
            if (IsWholeGameObjectRollback(header))
                header.GameObjectNode.MarkedForRollback = !header.GameObjectNode.MarkedForRollback;
            else if (IsWholeComponentRollback(header))
                header.MarkedForRollback = !header.MarkedForRollback;
            else
            {
                var fields = RollbackFieldsUnder(header);
                var mark = !fields.All(r => r.MarkedForRollback);
                foreach (var row in fields) row.MarkedForRollback = mark;
            }
            _table.RefreshItems();
            UpdatePendingRollbacks();
        }

        private bool IsHeaderMarkedForRollback(MockRow header)
        {
            if (IsWholeGameObjectRollback(header)) return header.GameObjectNode.MarkedForRollback;
            if (IsWholeComponentRollback(header)) return header.MarkedForRollback;
            var fields = RollbackFieldsUnder(header);
            return fields.Count > 0 && fields.All(r => r.MarkedForRollback);
        }

        private List<MockRow> RollbackFieldsUnder(MockRow header) =>
            _currentRows.Where(r => CanRollbackField(r) && IsUnderHeader(r, header)).ToList();

        /// <summary>One write per file holding marks: the marked fields spliced back from A, the marked
        /// components / GameObjects restored from A (a GameObject with its components, a component with
        /// its GameObject when that is missing too), then only those files are rebuilt.</summary>
        private void ApplyRollbacks()
        {
            if (!CanRollback) return;

            var written = new List<string>();
            var problems = new List<string>();
            foreach (var fileNode in _roots.Where(root => CountRollbacks(root.id) > 0).Select(root => root.data).ToList())
            {
                var path = fileNode.FilePath;
                var fullPath = Path.Combine(GitFileReader.RepositoryRoot, path);
                var otherText = GitFileReader.ReadFileAtRevision(_revisionA, path);
                if (!File.Exists(fullPath) || otherText == null)
                {
                    problems.Add($"{path}: can't read the working tree or {_revisionA}");
                    continue;
                }

                var rows = MockDataSource.RowsByNodeId.GetValueOrDefault(fileNode.Id) ?? new List<MockRow>();
                var fieldRows = rows.Where(r => r.MarkedForRollback && !r.IsHeader)
                    .Select(r => new MockRow { FileId = r.FileId, Key = r.Key, ExtraKeys = r.ExtraKeys, Property = r.Property, Resolution = MockResolution.B })
                    .ToList();

                var restoreIds = new HashSet<long>();
                var deleteComponentIds = new HashSet<long>();
                var deleteGameObjectIds = new HashSet<long>();
                foreach (var header in rows.Where(r => r.IsHeader && r.MarkedForRollback))
                {
                    if (header.HeaderStateB == MockHeaderState.Added)
                    {
                        deleteComponentIds.Add(header.DocumentIdB);
                        continue;
                    }
                    restoreIds.Add(header.FileId);
                    var owner = header.OwnerHeaderRow?.GameObjectNode;
                    if (owner != null && owner.HeaderStateB == MockHeaderState.Deleted) restoreIds.Add(owner.FileId);
                }
                foreach (var go in MockDataSource.GameObjectNodesByNodeId.GetValueOrDefault(fileNode.Id)?.Where(n => n.MarkedForRollback) ?? Enumerable.Empty<MockNode>())
                {
                    if (go.HeaderStateB == MockHeaderState.Added)
                    {
                        deleteGameObjectIds.Add(go.FileId);
                        continue;
                    }
                    restoreIds.Add(go.FileId);
                    foreach (var component in MockDataSource.OwnRowsByNodeId.GetValueOrDefault(go.Id)?.Where(r => r.IsHeader && r.FileId != 0) ?? Enumerable.Empty<MockRow>())
                        restoreIds.Add(component.FileId);
                }

                var baseText = File.ReadAllText(fullPath);
                var (patched, skipped) = UnityYamlWriter.Apply(baseText, UnityYamlParser.Parse(baseText), otherText, UnityYamlParser.Parse(otherText),
                    fieldRows, deleteGameObjectIds, deleteComponentIds, restoreIds);

                File.WriteAllText(fullPath, patched);
                AssetDatabase.ImportAsset(GetProjectRelativePath(fullPath));
                written.Add(path);
                problems.AddRange(skipped.Select(s => $"{path}: {s}"));
                Debug.Log($"[UnityGitTool] Rollback {path} to {_revisionA}: {fieldRows.Count} field(s), {restoreIds.Count} restored document(s), {deleteGameObjectIds.Count + deleteComponentIds.Count} added object(s) deleted, {skipped.Count} skipped");
            }

            if (written.Count > 0) RefreshFiles(written);
            UpdatePendingRollbacks();

            if (problems.Count == 0)
                SetStatus($"Rolled back {written.Count} file(s) to {_revisionA}.");
            else
            {
                var more = problems.Count > 4 ? $" (+{problems.Count - 4} more)" : "";
                SetStatus($"Rolled back {written.Count} file(s), {problems.Count} item(s) need attention: {string.Join("; ", problems.Take(4))}{more}");
            }
        }
    }
}
