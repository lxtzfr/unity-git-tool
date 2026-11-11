using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Tree node of the Dead Entries tab: a file, or one prefab instance in it.</summary>
    internal sealed class DeadEntriesNode
    {
        public string Path;
        /// <summary>0 on a file's own node.</summary>
        public long InstanceId;
        public string Label;
        public string Icon;
        public List<DeadPrefabEntry> Entries;
    }

    /// <summary>The project's prefab instance entries whose target is gone from the source asset
    /// (<see cref="DeadPrefabEntries"/>): files and instances on the left, their entries on the right,
    /// cleanable per instance, per file or all at once. A tab of <see cref="UnityGitToolWindow"/>; the
    /// scan reads every prefab and scene, so it runs on the first show and on Rescan only.</summary>
    internal sealed class DeadEntriesPanel
    {
        private TreeView _tree;
        private MultiColumnListView _table;
        private Label _statusLabel;
        private Button _cleanAllButton;
        private List<SceneYamlRow> _rows = new();
        private Dictionary<string, List<DeadPrefabEntry>> _byFile;

        public VisualElement Build()
        {
            var root = new VisualElement { style = { flexGrow = 1 } };
            var split = new TwoPaneSplitView(0, 340, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            split.AddToClassList("gt-split");
            split.Add(BuildTreePanel());
            split.Add(BuildTablePanel());
            root.Add(split);
            root.Add(BuildFooter());
            return root;
        }

        private bool _scanScheduled;

        /// <summary>First show scans on the next editor update: its progress bar must not run inside
        /// CreateGUI. Not delayCall: it doesn't run while the Editor is unfocused.</summary>
        public void Show()
        {
            if (_byFile != null || _scanScheduled) return;
            _scanScheduled = true;
            EditorApplication.update += ScanOnce;
        }

        public void Hide()
        {
            EditorApplication.update -= ScanOnce;
            _scanScheduled = false;
        }

        private void ScanOnce()
        {
            EditorApplication.update -= ScanOnce;
            _scanScheduled = false;
            Rescan();
        }

        private void Rescan()
        {
            var result = DeadPrefabEntries.ScanProject();
            if (result == null && _byFile != null) return; // cancelled: keep the previous results
            _byFile = result ?? new Dictionary<string, List<DeadPrefabEntry>>();
            RebuildTree();
        }

        // --------------------------------------------------------------------------- tree --

        private VisualElement BuildTreePanel()
        {
            var pane = new VisualElement { style = { flexGrow = 1 } };
            pane.Add(UnityGitToolWindow.BuildPanelTitle("Files"));
            _tree = new TreeView
            {
                fixedItemHeight = 24,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                makeItem = BuildTreeRow,
                bindItem = (element, index) => BindTreeRow(element, _tree.GetItemDataForIndex<DeadEntriesNode>(index)),
            };
            _tree.AddToClassList("gt-tree");
            _tree.selectionChanged += selection => ShowRows(selection.FirstOrDefault() as DeadEntriesNode);
            pane.Add(_tree);
            return pane;
        }

        private void RebuildTree()
        {
            var nextId = 0;
            var roots = new List<TreeViewItemData<DeadEntriesNode>>();
            foreach (var (path, entries) in _byFile.OrderBy(kv => kv.Key))
            {
                var instances = entries.GroupBy(e => e.InstanceId)
                    .Select(g => new TreeViewItemData<DeadEntriesNode>(nextId++, new DeadEntriesNode
                    {
                        Path = path, InstanceId = g.Key, Label = g.First().InstanceName, Icon = "Prefab Icon", Entries = g.ToList(),
                    }))
                    .ToList();
                roots.Add(new TreeViewItemData<DeadEntriesNode>(nextId++, new DeadEntriesNode
                {
                    Path = path, Label = path, Entries = entries,
                    Icon = path.EndsWith(".unity") ? "SceneAsset Icon" : "Prefab Icon",
                }, instances));
            }
            _tree.SetRootItems(roots);
            _tree.Rebuild();
            ShowRows(null);
            UpdateStatus();
        }

        private static VisualElement BuildTreeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("gt-tree-row");
            var icon = new Image { scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("gt-tree-icon");
            icon.AddToClassList("gt-icon-leading");
            row.Add(icon);
            var label = new Label();
            label.AddToClassList("gt-tree-label");
            row.Add(label);
            var count = new Label();
            count.AddToClassList("gt-tree-label");
            count.style.opacity = 0.6f;
            row.Add(count);
            return row;
        }

        private static void BindTreeRow(VisualElement element, DeadEntriesNode node)
        {
            ((Image)element[0]).image = UnityGitToolWindow.GetIcon(node.Icon);
            ((Label)element[1]).text = node.Label;
            ((Label)element[2]).text = $"({node.Entries.Count})";
        }

        // -------------------------------------------------------------------------- table --

        private VisualElement BuildTablePanel()
        {
            var pane = new VisualElement { style = { flexGrow = 1 } };
            pane.Add(UnityGitToolWindow.BuildPanelTitle("Dead entries"));
            _table = new MultiColumnListView
            {
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                selectionType = SelectionType.None,
            };
            _table.columns.Add(new Column
            {
                title = "Entry", width = 220, minWidth = 90, stretchable = true,
                makeCell = SceneObjectsPanel.MakePropertyCell,
                bindCell = (element, index) => SceneObjectsPanel.BindPropertyCell(element, _rows[index]),
            });
            _table.columns.Add(new Column
            {
                title = "Target", width = 320, minWidth = 160, stretchable = true,
                makeCell = SceneObjectsPanel.MakeValueCell,
                bindCell = (element, index) => SceneObjectsPanel.BindValueCell(element, _rows[index]),
            });
            _table.AddToClassList("gt-table");
            pane.Add(_table);
            return pane;
        }

        /// <summary>A file: one header per instance; an instance: its own header. Each header cleans what it lists.</summary>
        private void ShowRows(DeadEntriesNode node)
        {
            _rows = new List<SceneYamlRow>();
            if (node != null)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(node.Path);
                if (asset != null && node.InstanceId == 0) EditorGUIUtility.PingObject(asset);
                foreach (var group in node.Entries.GroupBy(e => e.InstanceId))
                {
                    var entries = group.ToList();
                    _rows.Add(new SceneYamlRow
                    {
                        IsHeader = true,
                        Property = $"{entries[0].InstanceName} ({entries.Count})",
                        HeaderIcon = "Prefab Icon",
                        ActionLabel = "Clean",
                        ActionTooltip = $"Remove these {entries.Count} entr(ies) from {Path.GetFileName(node.Path)}",
                        Action = () => CleanFiles(new Dictionary<string, List<DeadPrefabEntry>> { [node.Path] = entries }),
                    });
                    _rows.AddRange(entries.Select(e => new SceneYamlRow { Property = e.Label, Value = e.TargetLabel }));
                }
            }
            _table.itemsSource = _rows;
            _table.Rebuild();
        }

        // ------------------------------------------------------------------------- footer --

        private VisualElement BuildFooter()
        {
            var footer = new VisualElement();
            footer.AddToClassList("gt-footer");
            var infoIcon = new Image { image = UnityGitToolWindow.GetIcon("console.infoicon"), scaleMode = ScaleMode.ScaleToFit };
            infoIcon.AddToClassList("gt-tree-icon");
            footer.Add(infoIcon);
            _statusLabel = new Label();
            footer.Add(_statusLabel);

            var spacer = new VisualElement();
            spacer.AddToClassList("gt-flex-spacer");
            footer.Add(spacer);

            var rescan = new Button(Rescan) { text = "Rescan", tooltip = "Read every prefab and scene under Assets/ again (Samples and _Recovery skipped)" };
            rescan.AddToClassList("gt-pill");
            footer.Add(rescan);
            _cleanAllButton = new Button(() => CleanFiles(_byFile))
            {
                tooltip = "Remove every listed entry from its file. Unity ignores them on load; its own save only drops the added objects.",
            };
            _cleanAllButton.AddToClassList("gt-pill");
            footer.Add(_cleanAllButton);
            return footer;
        }

        private void UpdateStatus()
        {
            var total = _byFile?.Values.Sum(l => l.Count) ?? 0;
            _statusLabel.text = total == 0
                ? "No dead prefab entry (Samples and _Recovery skipped)"
                : $"{total} dead prefab entr(ies) in {_byFile.Count} file(s): their target no longer exists in the source asset";
            _cleanAllButton.text = $"Clean all ({total})";
            _cleanAllButton.SetEnabled(total > 0);
        }

        /// <summary>Writes the files, then rescans only them: their remaining entries replace the old ones.</summary>
        private void CleanFiles(IReadOnlyDictionary<string, List<DeadPrefabEntry>> byFile)
        {
            var total = byFile.Values.Sum(l => l.Count);
            if (!EditorUtility.DisplayDialog("Unity Git Tool", $"Remove {total} dead entr(ies) from {byFile.Count} file(s)?", "Clean", "Cancel")) return;

            var paths = byFile.Keys.ToList();
            var problems = DeadPrefabEntries.Clean(byFile);
            var sources = new YamlHierarchy.SourceCache();
            foreach (var path in paths)
            {
                var dead = DeadPrefabEntries.Find(UnityYamlDiffBuilder.IndexByFileId(UnityYamlParser.Parse(File.ReadAllText(path))), sources);
                if (dead.Count > 0) _byFile[path] = dead;
                else _byFile.Remove(path);
            }
            RebuildTree();
            if (problems.Count > 0)
                EditorUtility.DisplayDialog("Unity Git Tool", $"{problems.Count} problem(s), see the Console.", "OK");
        }
    }
}
