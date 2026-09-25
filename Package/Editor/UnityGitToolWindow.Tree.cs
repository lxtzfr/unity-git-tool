using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Left panel: the files > GameObjects/documents > components tree. A second instance
    /// of this same tree (see <see cref="_resultTree"/>) sits to the right of the table, previewing
    /// the resolved hierarchy — both are built from the exact same <c>roots</c> data
    /// (<see cref="RefreshTree"/> populates both in one pass) and kept in sync by selection (see
    /// <see cref="SyncTreeSelection"/>), so either one can be used to navigate.</summary>
    public partial class UnityGitToolWindow
    {
        private TreeView _tree;
        private TreeView _resultTree;
        // Re-entrancy guard for SyncTreeSelection: setting one tree's selection from the other's
        // selectionChanged handler would otherwise fire that tree's own selectionChanged right back,
        // ping-ponging forever.
        private bool _syncingTreeSelection;

        private VisualElement BuildTreePanel(string title, bool isResultTree, out TreeView treeView)
        {
            var pane = new VisualElement { style = { flexGrow = 1 } };
            pane.Add(BuildPanelTitle(title));

            var tree = new TreeView
            {
                fixedItemHeight = 24,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                makeItem = BuildTreeRow,
            };
            tree.AddToClassList("gt-tree");
            tree.bindItem = (element, index) => BindTreeRow(element, tree.GetItemDataForIndex<MockNode>(index), isResultTree);
            tree.selectionChanged += selection => OnTreeSelectionChanged(tree, selection);
            pane.Add(tree);
            treeView = tree;
            return pane;
        }

        /// <summary>Selecting a node in either tree selects the same node (by id — both trees share
        /// the same <c>roots</c>, so ids line up) in the other one too, and refreshes the table off
        /// it — see <see cref="_syncingTreeSelection"/> for why the propagated call doesn't loop.</summary>
        private void OnTreeSelectionChanged(TreeView source, IEnumerable<object> selection)
        {
            if (_syncingTreeSelection) return;
            if (selection.FirstOrDefault() is not MockNode node) return;

            _selectedNode = node;
            // OwnRowsByNodeId, not RowsByNodeId — a file node's (and an ancestor GameObject's)
            // RowsByNodeId entry aggregates every descendant's rows too (see BuildFileNode /
            // BuildGameObjectSubtree), which is what ApplyResolution needs but would otherwise
            // make a child GameObject's own components read as if they belonged to its parent,
            // just because the parent happens to be selected. The table only ever shows a node's
            // own rows — selecting a file, or a GameObject that's only a structural ancestor of
            // the real change, shows nothing until you drill into the actual changed object.
            if (node.Kind == MockNodeKind.GameObject && MockDataSource.OwnRowsByNodeId.TryGetValue(node.Id, out var rows))
            {
                // A synthetic header row for the GameObject itself — carries "Delete GameObject" (see
                // Table.cs), same idea as a component's own header row but for the object as a whole,
                // which otherwise has no row of its own to hang that action on.
                var gameObjectHeader = new MockRow
                {
                    IsHeader = true, Property = node.Label, HeaderIcon = "GameObject Icon", GameObjectNode = node,
                    HeaderStateA = node.HeaderStateA, HeaderStateB = node.HeaderStateB,
                };
                _currentRows = new List<MockRow> { gameObjectHeader };
                _currentRows.AddRange(rows);

                // Each field row's OwnerHeaderRow is whichever header (this GameObject's own, or the
                // most recent component header) it fell under. A component header row also gets its
                // OwnerHeaderRow set — to the GameObject header, always — so that deleting the whole
                // GameObject is reflected on its component headers too, not just its own row (see
                // MockRow.OwnerHeaderRow / IsMarkedForDeletion).
                var currentHeader = gameObjectHeader;
                foreach (var row in _currentRows)
                {
                    if (row == gameObjectHeader) continue; // the root header has no owner of its own
                    if (row.IsHeader)
                    {
                        row.OwnerHeaderRow = gameObjectHeader;
                        currentHeader = row;
                        continue;
                    }
                    row.OwnerHeaderRow = currentHeader;
                }
            }
            else
            {
                _currentRows = new List<MockRow>();
            }
            RefreshTable();

            var other = source == _tree ? _resultTree : _tree;
            if (other == null) return;
            _syncingTreeSelection = true;
            try { other.SetSelectionById(node.Id); }
            finally { _syncingTreeSelection = false; }
        }

        /// <summary>Rebuilds the left tree. When A is Working Tree (the merge/rebase-resolution case —
        /// see "Auto-config merge branches"), the file set is exactly git status's: everything that
        /// differs from HEAD (<c>GetChangedFiles(A, HEAD)</c> — matches its "modified"/"added"/"deleted"
        /// entries) union <see cref="GitFileReader.GetConflictedFiles"/> (matches its "Unmerged paths").
        /// B (usually a conflict ref like REBASE_HEAD) is deliberately NOT used to pick which files
        /// appear — it's an old, already-superseded commit, so diffing against it directly pulls in
        /// files that only differ because of that, not because of anything the current operation
        /// touched; B is still used below to read each listed file's own "Theirs" content. Otherwise
        /// (comparing two arbitrary revisions, nothing to do with a live conflict) the file set is
        /// just <c>GetChangedFiles(A, B)</c> as you'd expect from a plain diff. Filtered to scene/prefab
        /// files either way — the only ones this tool knows how to open. <see cref="MockNode.HasConflict"/>
        /// (the warning icon), not list membership, is what then distinguishes a real conflict from a
        /// change that auto-merged cleanly, same as Rider's Commit/Changes view (as opposed to its
        /// separate conflicts-only "Resolve Conflicts" dialog, which this tool doesn't have —
        /// everything lives in one list).</summary>
        private void RefreshTree()
        {
            MockDataSource.RowsByNodeId.Clear();
            MockDataSource.OwnRowsByNodeId.Clear();
            MockDataSource.GameObjectNodesByNodeId.Clear();
            _currentRows = new List<MockRow>();
            _selectedNode = null;

            var nextId = 0;
            int NextId() => nextId++;

            // Which of the changed files git itself still considers unresolved — see
            // UnityYamlDiffBuilder's class header for why a per-field IsConflict needs this, not just
            // "both sides have it and it differs".
            var conflictedPaths = new HashSet<string>(GitFileReader.GetConflictedFiles());

            var filesByPath = new Dictionary<string, GitChangedFile>();
            if (_revisionA == WorkingTree)
            {
                // A vs B (usually a conflict ref like REBASE_HEAD) is NOT the right file-listing source
                // here: it also picks up files that only differ from that ref because it's an old,
                // already-superseded commit — noise `git status` never shows. HEAD (what's actually
                // changed by the current operation) plus the conflicted set is exactly git status's
                // file list; B is still used below for each file's own content, just not to pick which
                // files appear.
                foreach (var file in GitFileReader.GetChangedFiles(_revisionA, "HEAD"))
                    filesByPath[file.Path] = file;

                foreach (var path in conflictedPaths)
                    if (!filesByPath.ContainsKey(path)) filesByPath[path] = new GitChangedFile(path, null, GitChangeStatus.Modified);
            }
            else
            {
                foreach (var file in GitFileReader.GetChangedFiles(_revisionA, _revisionB))
                    filesByPath[file.Path] = file;
            }

            var roots = new List<TreeViewItemData<MockNode>>();
            foreach (var file in filesByPath.Values)
            {
                MockNodeKind kind;
                var extension = Path.GetExtension(file.Path);
                if (extension == ".unity") kind = MockNodeKind.SceneFile;
                else if (extension == ".prefab") kind = MockNodeKind.PrefabFile;
                else continue;

                var badge = file.Status switch
                {
                    GitChangeStatus.Added => MockBadge.Added,
                    GitChangeStatus.Deleted => MockBadge.Removed,
                    _ => MockBadge.Modified,
                };
                var fileIsConflicted = conflictedPaths.Contains(file.Path);

                // A/B during conflict resolution: for a genuinely conflicted file, B stays the conflict
                // ref (REBASE_HEAD etc.) so its content is real "Theirs" to pick against. For any other
                // "Modified" file, working tree is already whatever this operation settled it to — often
                // byte-identical to that same conflict ref, since it's an old, already-superseded commit
                // (see RefreshTree's own doc comment) — so there's nothing left to compare there. HEAD
                // (what's actually different because of this operation) is what shows something. In
                // this fallback, unlike every other A/B pairing in this tool, B (HEAD) is the OLDER side
                // and A (Working Tree) the newer one — bIsOlderBaseline tells BuildFileNode to flip its
                // Added/Removed reading accordingly (see its doc comment), or a GameObject only this
                // operation added would read backwards as "Removed".
                var bIsOlderBaseline = _revisionA == WorkingTree && !fileIsConflicted;
                var contentRevisionB = bIsOlderBaseline ? "HEAD" : _revisionB;

                // A renamed file's old path only exists on the A side — GetChangedFiles already
                // reports the new path for B, so read A back at its OldPath when there is one.
                var contentA = GitFileReader.ReadFileAtRevision(_revisionA, file.OldPath ?? file.Path);
                var contentB = GitFileReader.ReadFileAtRevision(contentRevisionB, file.Path);

                roots.Add(UnityYamlDiffBuilder.BuildFileNode(
                    NextId, MockDataSource.RowsByNodeId, MockDataSource.OwnRowsByNodeId, MockDataSource.GameObjectNodesByNodeId,
                    Path.GetFileName(file.Path), file.Path, kind, badge, contentA, contentB, fileIsConflicted, bIsOlderBaseline));
            }

            // Same `roots` data bound to both trees — see the class header for why: they share ids,
            // so selection syncs by id and a build-once diff isn't duplicated per tree. _resultTree is
            // null outside Merge mode (see UnityGitToolWindow.cs's CreateGUI) — nothing to preview.
            _tree.SetRootItems(roots);
            _tree.Rebuild();
            _tree.ExpandRootItems();
            if (_resultTree != null)
            {
                _resultTree.SetRootItems(roots);
                _resultTree.Rebuild();
                _resultTree.ExpandRootItems();
            }
            RefreshTable();
        }

        private static VisualElement BuildTreeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("gt-tree-row");

            var typeIcon = new Image { scaleMode = ScaleMode.ScaleToFit };
            typeIcon.AddToClassList("gt-tree-icon");
            typeIcon.AddToClassList("gt-icon-leading");
            row.Add(typeIcon);

            var label = new Label();
            label.AddToClassList("gt-tree-label");
            row.Add(label);

            var conflictIcon = new Image { scaleMode = ScaleMode.ScaleToFit, image = GetIcon("console.warnicon"), tooltip = "Contains unresolved conflicts" };
            conflictIcon.AddToClassList("gt-tree-icon");
            conflictIcon.AddToClassList("gt-icon-trailing");
            row.Add(conflictIcon);

            var badgeLabel = new Label();
            badgeLabel.AddToClassList("gt-chip");
            badgeLabel.AddToClassList("gt-icon-trailing");
            row.Add(badgeLabel);
            return row;
        }

        /// <summary>Shared binder for both trees (see the class header), but the two read differently:
        /// the Files tree is a plain git-status view, so it keeps the A/M/D chip; the Result tree is a
        /// preview of the merge outcome, where a same-meaning chip sitting next to a name that's
        /// ALSO turning red on delete read as two overlapping signals for one thing — collapsed here
        /// into one: the name itself carries the color (green/orange/red for Added/Modified/Deleted,
        /// <see cref="MockNode.MarkedForDeletion"/> forcing red regardless of the underlying badge since
        /// that's the more final of the two), chip hidden entirely. A deleted node's label reads
        /// struck-through rather than disappearing — it's still shown (and still selectable) until
        /// Apply actually removes it from the file. No delete button here in either tree — "Delete
        /// GameObject" lives on the synthetic header row the table prepends when this node is selected
        /// (see OnTreeSelectionChanged / UnityGitToolWindow.Table.cs), so this method is the only thing
        /// that needs to react to MarkedForDeletion changing.</summary>
        private void BindTreeRow(VisualElement element, MockNode node, bool isResultTree)
        {
            var typeIcon = (Image)element[0];
            var label = (Label)element[1];
            var conflictIcon = (Image)element[2];
            var badgeLabel = (Label)element[3];

            typeIcon.image = GetIcon(node.Kind switch
            {
                MockNodeKind.SceneFile => "SceneAsset Icon",
                MockNodeKind.PrefabFile => IsPrefabVariant(node.FilePath) ? "PrefabVariant Icon" : "Prefab Icon",
                _ => "GameObject Icon",
            });
            label.text = node.Label;
            conflictIcon.style.display = _isMerge && node.HasConflict ? DisplayStyle.Flex : DisplayStyle.None;

            if (isResultTree)
            {
                badgeLabel.style.display = DisplayStyle.None;
                label.RemoveFromClassList("gt-tree-label-deleted"); // opacity dimming isn't used here — color alone carries the state
                var stateColor = node.MarkedForDeletion ? BadgeRed : node.Badge switch
                {
                    MockBadge.Added => BadgeGreen,
                    MockBadge.Removed => BadgeRed,
                    MockBadge.Modified => BadgeOrange,
                    _ => (Color?)null,
                };
                if (stateColor.HasValue) label.style.color = stateColor.Value;
                else label.style.color = StyleKeyword.Null;
                return;
            }

            label.style.color = StyleKeyword.Null;
            label.EnableInClassList("gt-tree-label-deleted", node.MarkedForDeletion);

            badgeLabel.style.display = node.Badge == MockBadge.None ? DisplayStyle.None : DisplayStyle.Flex;
            badgeLabel.text = node.Badge switch
            {
                MockBadge.Added => "A",
                MockBadge.Removed => "D",
                MockBadge.Modified => "M",
                _ => "",
            };
            badgeLabel.tooltip = node.Badge.ToString();
            var badgeColor = node.Badge switch
            {
                MockBadge.Added => BadgeGreen,
                MockBadge.Removed => BadgeRed,
                MockBadge.Modified => BadgeOrange,
                _ => Color.clear,
            };
            badgeLabel.style.color = badgeColor;
        }

        /// <summary>Whether the prefab currently on disk at <paramref name="path"/> is a Prefab
        /// Variant (as opposed to a regular prefab) — read from the live AssetDatabase, same source
        /// Unity's own Project window icon comes from, rather than re-deriving it from the parsed
        /// YAML (a variant's file shape — a PrefabInstance document plus overrides instead of a full
        /// object tree — isn't something this tool's parser models). Reflects whatever's on disk right
        /// now, so it's only meaningful for the Working Tree side, same caveat as every other
        /// AssetDatabase-backed lookup in this tool (see UnityYamlValueConverter's reference
        /// resolution) — acceptable for a tree icon, which is cosmetic.</summary>
        private static bool IsPrefabVariant(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return asset != null && PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.Variant;
        }
    }
}
