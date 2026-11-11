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
        private List<TreeViewItemData<MockNode>> _roots = new();
        // Never reset: RefreshFiles rebuilds one file while the others keep their ids.
        private int _nextNodeId;
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
            if (node.Kind is MockNodeKind.GameObject or MockNodeKind.PrefabInstance or MockNodeKind.PrefabInstanceObject && MockDataSource.OwnRowsByNodeId.TryGetValue(node.Id, out var rows))
            {
                // A synthetic header row for the GameObject/PrefabInstance itself — carries "Delete
                // GameObject" (see Table.cs; a PrefabInstance node has no delete action of its own in
                // this pass, so GameObjectNode stays null for it — same idea as a component's own
                // header row but for the object as a whole, which otherwise has no row of its own to
                // hang that action on.
                var gameObjectHeader = new MockRow
                {
                    IsHeader = true, Property = node.Label,
                    HeaderIcon = node.Kind == MockNodeKind.PrefabInstance ? "Prefab Icon" : "GameObject Icon",
                    GameObjectNode = node.Kind == MockNodeKind.GameObject ? node : null,
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

        /// <summary>Rebuilds every file's nodes (revision change, Refresh), keeping the selection,
        /// expansion and scroll.</summary>
        private void RefreshTree()
        {
            var state = CaptureTreeState();
            MockDataSource.RowsByNodeId.Clear();
            MockDataSource.OwnRowsByNodeId.Clear();
            MockDataSource.GameObjectNodesByNodeId.Clear();

            var conflictedPaths = new HashSet<string>(GitFileReader.GetConflictedFiles());
            _roots = ListChangedFiles(conflictedPaths).Values
                .Select(file => BuildFileItem(file, conflictedPaths))
                .Where(item => item.HasValue)
                .Select(item => item.Value)
                .ToList();
            ApplyRoots(state);
        }

        /// <summary>Rebuilds only <paramref name="paths"/>' nodes (after a write to them), keeping every
        /// other file's nodes, rows and marks, and the selection / expansion / scroll. A path git no
        /// longer lists (fully rolled back) leaves the tree.</summary>
        private void RefreshFiles(IEnumerable<string> paths)
        {
            var state = CaptureTreeState();
            var conflictedPaths = new HashSet<string>(GitFileReader.GetConflictedFiles());
            var filesByPath = ListChangedFiles(conflictedPaths);

            var refreshed = paths.Distinct().ToList();
            foreach (var path in refreshed)
            {
                var index = _roots.FindIndex(root => root.data.FilePath == path);
                if (index >= 0) ForgetNodes(_roots[index]);

                var item = filesByPath.TryGetValue(path, out var file) ? BuildFileItem(file, conflictedPaths) : null;
                if (index >= 0 && item.HasValue) _roots[index] = item.Value;
                else if (index >= 0) _roots.RemoveAt(index);
                else if (item.HasValue) _roots.Add(item.Value);
            }
            Debug.Log($"[UnityGitTool] Refreshed {string.Join(", ", refreshed)}");
            ApplyRoots(state);
        }

        private static void ForgetNodes(TreeViewItemData<MockNode> item)
        {
            MockDataSource.RowsByNodeId.Remove(item.id);
            MockDataSource.OwnRowsByNodeId.Remove(item.id);
            MockDataSource.GameObjectNodesByNodeId.Remove(item.id);
            foreach (var child in item.children) ForgetNodes(child);
        }

        /// <summary>The file set: when A is Working Tree (the merge/rebase-resolution case), exactly git
        /// status's — everything that differs from HEAD plus the conflicted files. B (usually a conflict
        /// ref like REBASE_HEAD) is an old, already-superseded commit: diffing against it pulls in files
        /// that only differ because of that; it is still read for each file's "Theirs" content.
        /// Otherwise a plain A vs B diff. <see cref="MockNode.HasConflict"/>, not membership, marks a
        /// real conflict.</summary>
        private Dictionary<string, GitChangedFile> ListChangedFiles(HashSet<string> conflictedPaths)
        {
            var filesByPath = new Dictionary<string, GitChangedFile>();
            if (_revisionA == WorkingTree)
            {
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
            return filesByPath;
        }

        /// <summary>One file's subtree, or null for a file this tool can't open (not a scene / prefab).</summary>
        private TreeViewItemData<MockNode>? BuildFileItem(GitChangedFile file, HashSet<string> conflictedPaths)
        {
            var extension = Path.GetExtension(file.Path);
            MockNodeKind kind;
            if (extension == ".unity") kind = MockNodeKind.SceneFile;
            else if (extension == ".prefab") kind = MockNodeKind.PrefabFile;
            else return null;

            var badge = file.Status switch
            {
                GitChangeStatus.Added => MockBadge.Added,
                GitChangeStatus.Deleted => MockBadge.Removed,
                _ => MockBadge.Modified,
            };
            var fileIsConflicted = conflictedPaths.Contains(file.Path);

            // During conflict resolution a conflicted file keeps B = the conflict ref (real "Theirs").
            // Any other modified file is already settled in the working tree, often identical to that
            // ref, so it is compared to HEAD instead — there B is the OLDER side, which bIsOlderBaseline
            // tells BuildFileNode so a GameObject this operation added doesn't read as "Removed".
            var bIsOlderBaseline = _revisionA == WorkingTree && !fileIsConflicted;
            var contentRevisionB = bIsOlderBaseline ? "HEAD" : _revisionB;

            // A renamed file only exists at its OldPath on the A side.
            var contentA = GitFileReader.ReadFileAtRevision(_revisionA, file.OldPath ?? file.Path);
            var contentB = GitFileReader.ReadFileAtRevision(contentRevisionB, file.Path);

            return UnityYamlDiffBuilder.BuildFileNode(
                () => _nextNodeId++, MockDataSource.RowsByNodeId, MockDataSource.OwnRowsByNodeId, MockDataSource.GameObjectNodesByNodeId,
                Path.GetFileName(file.Path), file.Path, kind, badge, contentA, contentB, fileIsConflicted, bIsOlderBaseline);
        }

        /// <summary>Node ids are regenerated on every build, so the view state is carried over by a key
        /// stable across builds: each node's chain of (file path | FileId + label) from the root.</summary>
        private sealed class TreeState
        {
            public readonly HashSet<string> Expanded = new();
            public readonly HashSet<string> Known = new();
            public string Selected;
            public Vector2 Scroll;
        }

        private static string NodeKey(string parentKey, MockNode node) =>
            parentKey + "/" + (node.Kind is MockNodeKind.SceneFile or MockNodeKind.PrefabFile ? node.FilePath : $"{node.FileId}:{node.Label}");

        private TreeState CaptureTreeState()
        {
            var state = new TreeState();
            if (_tree == null) return state;
            void Walk(IEnumerable<TreeViewItemData<MockNode>> items, string parentKey)
            {
                foreach (var item in items)
                {
                    var key = NodeKey(parentKey, item.data);
                    state.Known.Add(key);
                    if (_tree.IsExpanded(item.id)) state.Expanded.Add(key);
                    if (_selectedNode?.Id == item.id) state.Selected = key;
                    Walk(item.children, key);
                }
            }
            Walk(_roots, "");
            state.Scroll = _tree.Q<ScrollView>()?.scrollOffset ?? Vector2.zero;
            return state;
        }

        /// <summary>Binds <see cref="_roots"/> to both trees (they share ids, so selection syncs by id;
        /// _resultTree is null outside Merge mode) and restores <paramref name="state"/>. A file the
        /// state doesn't know (first build, newly listed) opens expanded.</summary>
        private void ApplyRoots(TreeState state)
        {
            _currentRows = new List<MockRow>();
            _selectedNode = null;

            var expandIds = new List<int>();
            int? selectId = null;
            void Walk(IEnumerable<TreeViewItemData<MockNode>> items, string parentKey, bool isRoot)
            {
                foreach (var item in items)
                {
                    var key = NodeKey(parentKey, item.data);
                    if (state.Expanded.Contains(key) || (isRoot && !state.Known.Contains(key))) expandIds.Add(item.id);
                    if (key == state.Selected) selectId = item.id;
                    Walk(item.children, key, false);
                }
            }
            Walk(_roots, "", true);

            foreach (var tree in new[] { _tree, _resultTree })
            {
                if (tree == null) continue;
                tree.SetRootItems(_roots);
                tree.Rebuild();
                foreach (var id in expandIds) tree.ExpandItem(id, false, false);
                tree.RefreshItems();
            }
            RefreshTable();

            if (selectId.HasValue) _tree.SetSelectionById(selectId.Value); // refills the table through OnTreeSelectionChanged
            var scrollView = _tree.Q<ScrollView>();
            scrollView?.schedule.Execute(() => scrollView.scrollOffset = state.Scroll);
            UpdatePendingRollbacks();
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

            var rollbackCount = new VisualElement { tooltip = "Marked for rollback, not applied yet" };
            rollbackCount.AddToClassList("gt-rollback-count");
            rollbackCount.Add(new Image { image = GetIcon("Refresh"), scaleMode = ScaleMode.ScaleToFit });
            rollbackCount.Add(new Label());
            row.Add(rollbackCount);
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
            var rollbackCount = element[4];

            var pending = isResultTree ? 0 : CountRollbacks(node.Id);
            rollbackCount.style.display = pending > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            rollbackCount.Q<Label>().text = pending.ToString();

            typeIcon.image = GetIcon(node.Kind switch
            {
                MockNodeKind.SceneFile => "SceneAsset Icon",
                MockNodeKind.PrefabFile => IsPrefabVariant(node.FilePath) ? "PrefabVariant Icon" : "Prefab Icon",
                MockNodeKind.PrefabInstance => "Prefab Icon",
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
