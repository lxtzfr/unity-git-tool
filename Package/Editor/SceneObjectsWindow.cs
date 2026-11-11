using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>The open scenes' saved files, read from their YAML like the diff window reads a revision:
    /// one node per scene over its GameObject tree on the left, Property | Value of the selection on the right (read-only).
    /// Selection is synced with the Hierarchy through each object's GlobalObjectId.</summary>
    public class SceneObjectsWindow : EditorWindow
    {
        private TreeView _tree;
        private MultiColumnListView _table;
        private Label _statusLabel;
        private Button _cleanAllButton;
        private readonly Dictionary<string, List<DeadPrefabEntry>> _deadByScene = new();
        private List<SceneYamlRow> _rows = new();
        private int _gameObjectCount;
        // (scene guid, targetObjectId, targetPrefabId) of a node's GlobalObjectId -> tree id, for
        // Hierarchy -> tree and to keep expansion across reloads. A scene's own node is (guid, 0, 0).
        private readonly Dictionary<(string, ulong, ulong), int> _treeIdByObject = new();
        // Our own tree selection pushes Selection.activeObject, which fires selectionChanged straight back.
        private bool _syncingSelection;
        // viewController / SetSelectionById throw until the first SetRootItems.
        private bool _treeBuilt;

        [MenuItem("Unity Git Tool/Scene Objects", priority = 1)]
        private static void Open()
        {
            var window = GetWindow<SceneObjectsWindow>("Scene Objects");
            window.minSize = new Vector2(700, 360);
        }

        private void OnEnable()
        {
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneDirtied += OnSceneDirtied;
            Selection.selectionChanged += OnEditorSelectionChanged;
        }

        private void OnDisable()
        {
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneDirtied -= OnSceneDirtied;
            Selection.selectionChanged -= OnEditorSelectionChanged;
            EditorApplication.update -= ReloadOnce;
            _reloadScheduled = false;
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            var stylesheet = Resources.Load<StyleSheet>("UnityGitTool");
            if (stylesheet != null) root.styleSheets.Add(stylesheet);

            var split = new TwoPaneSplitView(0, 340, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            split.AddToClassList("gt-split");
            split.Add(BuildTreePanel());
            split.Add(BuildTablePanel());
            root.Add(split);
            root.Add(BuildFooter());

            Reload();
            OnEditorSelectionChanged();
        }

        private void OnActiveSceneChanged(Scene previous, Scene next) => _tree?.RefreshItems(); // the bold label moves
        private void OnSceneOpened(Scene scene, OpenSceneMode mode) => ScheduleReload();
        private void OnSceneClosed(Scene scene) => ScheduleReload();
        private void OnSceneSaved(Scene scene) => ScheduleReload();

        // sceneClosed fires while the scene is still listed, and a Save All raises one event per scene:
        // reload once, on the next editor update. Not delayCall: it doesn't run while the Editor is unfocused.
        // NonSerialized: a domain reload keeps a window's private fields but drops the pending callback.
        [System.NonSerialized] private bool _reloadScheduled;

        private void ScheduleReload()
        {
            if (_reloadScheduled) return;
            _reloadScheduled = true;
            EditorApplication.update += ReloadOnce;
        }

        private void ReloadOnce()
        {
            EditorApplication.update -= ReloadOnce;
            _reloadScheduled = false;
            Reload();
        }
        private void OnSceneDirtied(Scene scene) => UpdateStatus();

        // --------------------------------------------------------------------------- tree --

        private VisualElement BuildTreePanel()
        {
            var pane = new VisualElement { style = { flexGrow = 1 } };
            pane.Add(UnityGitToolWindow.BuildPanelTitle("Scene"));

            _tree = new TreeView
            {
                fixedItemHeight = 24,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                makeItem = BuildTreeRow,
                bindItem = (element, index) => BindTreeRow(element, _tree.GetItemDataForIndex<SceneYamlNode>(index)),
            };
            _treeBuilt = false;
            _tree.AddToClassList("gt-tree");
            _tree.selectionChanged += OnTreeSelectionChanged;
            pane.Add(_tree);
            return pane;
        }

        /// <summary>Re-reads every open scene's file from disk and rebuilds the tree, keeping what was
        /// expanded (tree ids are rebuilt, so expansion is matched by GlobalObjectId).</summary>
        private void Reload()
        {
            if (_tree == null) return;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var sources = new YamlHierarchy.SourceCache(); // shared: scenes often place the same prefabs
            var nextId = 0;
            var roots = new List<TreeViewItemData<SceneYamlNode>>();
            _deadByScene.Clear();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                var hasFile = !string.IsNullOrEmpty(scene.path) && File.Exists(scene.path);
                var hierarchy = hasFile
                    ? YamlHierarchy.Resolve(UnityYamlDiffBuilder.IndexByFileId(UnityYamlParser.Parse(File.ReadAllText(scene.path))), sources)
                    : null;
                var label = string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;
                if (!hasFile) label += " (never saved)";
                else if (!scene.isLoaded) label += " (not loaded)";
                var dead = hierarchy != null ? DeadPrefabEntries.Find(hierarchy.ById, sources) : new List<DeadPrefabEntry>();
                if (dead.Count > 0) _deadByScene[scene.path] = dead;
                var scenePath = scene.path;
                roots.Add(SceneYamlTree.BuildScene(label, scene.path, hierarchy, () => nextId++, dead, entries => CleanDeadEntries(scenePath, entries)));
            }

            var expandedObjects = new HashSet<(string, ulong, ulong)>();
            if (_treeBuilt)
                foreach (var (key, id) in _treeIdByObject)
                    if (_tree.IsExpanded(id)) expandedObjects.Add(key);

            _treeIdByObject.Clear();
            _gameObjectCount = 0;
            IndexNodes(roots);
            Debug.Log($"[UnityGitTool] Scene Objects: read {roots.Count} scenes + {sources.FilesRead} prefab files, {_gameObjectCount} GameObjects, {DeadCount} dead prefab entries in {watch.ElapsedMilliseconds} ms");

            var firstBuild = !_treeBuilt;
            _treeBuilt = true;
            _tree.SetRootItems(roots);
            _tree.Rebuild();
            if (firstBuild) _tree.ExpandRootItems();
            else
                foreach (var (key, id) in _treeIdByObject)
                    if (expandedObjects.Contains(key)) _tree.ExpandItem(id);

            ShowRows(null);
            UpdateStatus();
        }

        private void IndexNodes(IEnumerable<TreeViewItemData<SceneYamlNode>> items)
        {
            foreach (var item in items)
            {
                if (item.data.GameObject != null) _gameObjectCount++;
                _treeIdByObject.TryAdd(ObjectKey(item.data), item.id);
                IndexNodes(item.children);
            }
        }

        private int DeadCount => _deadByScene.Values.Sum(l => l.Count);

        private void UpdateStatus()
        {
            if (_statusLabel == null) return;
            var dirty = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isDirty).Select(s => s.name).ToList();
            var dead = DeadCount;
            _statusLabel.text = $"{SceneManager.sceneCount} scene(s), {_gameObjectCount} GameObjects (saved files)" +
                (dead > 0 ? $" — {dead} dead prefab entr(ies), marked in the tree" : "") +
                (dirty.Count > 0 ? $" — unsaved changes in {string.Join(", ", dirty)} are not shown until saved" : "");
            if (_cleanAllButton != null)
            {
                _cleanAllButton.text = $"Clean dead entries ({dead})";
                _cleanAllButton.style.display = dead > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void CleanDeadEntries(string scenePath, List<DeadPrefabEntry> entries)
        {
            DeadPrefabEntries.Clean(scenePath, entries, out var skipped);
            foreach (var problem in skipped) Debug.LogWarning($"[UnityGitTool] {problem}");
            Reload();
        }

        private void CleanAllDeadEntries()
        {
            foreach (var (path, entries) in _deadByScene.ToList())
            {
                DeadPrefabEntries.Clean(path, entries, out var skipped);
                foreach (var problem in skipped) Debug.LogWarning($"[UnityGitTool] {problem}");
            }
            Reload();
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

            var warning = new Image { image = UnityGitToolWindow.GetIcon("console.warnicon.sml"), scaleMode = ScaleMode.ScaleToFit };
            warning.AddToClassList("gt-tree-icon");
            row.Add(warning);
            return row;
        }

        private static void BindTreeRow(VisualElement element, SceneYamlNode node)
        {
            ((Image)element[0]).image = UnityGitToolWindow.GetIcon(node.Icon);
            var label = (Label)element[1];
            label.text = node.Label;
            // Like the Hierarchy: the active scene's name in bold.
            label.EnableInClassList("gt-row-header-label", node.GameObject == null && node.ScenePath == SceneManager.GetActiveScene().path);
            var warning = (Image)element[2];
            warning.style.display = node.DeadEntries.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            warning.tooltip = node.DeadEntries.Count > 0 ? $"{node.DeadEntries.Count} dead prefab entr(ies): their target no longer exists in the source asset" : null;
        }

        // -------------------------------------------------------------------- selection --

        /// <summary>The (targetObjectId, targetPrefabId) pair Unity's GlobalObjectId gives the loaded
        /// object: the scene fileID for a plain GameObject; for an object inside a prefab instance, its
        /// fileID in the source prefab (nested ones combined), with the scene instance's fileID as prefab id.</summary>
        private static (string, ulong, ulong) ObjectKey(SceneYamlNode node) => node.GameObject == null
            ? (node.SceneGuid ?? node.Label, 0UL, 0UL)
            : (node.SceneGuid, (ulong)node.GameObject.GlobalObjectId, (ulong)node.GameObject.GlobalPrefabId);

        private void OnTreeSelectionChanged(IEnumerable<object> selection)
        {
            var node = selection.FirstOrDefault() as SceneYamlNode;
            ShowRows(node);
            if (_syncingSelection || node?.GameObject == null || string.IsNullOrEmpty(node.SceneGuid)) return;

            var (sceneGuid, objectId, prefabId) = ObjectKey(node);
            if (!GlobalObjectId.TryParse($"GlobalObjectId_V1-2-{sceneGuid}-{objectId}-{prefabId}", out var globalId)) return;
            var target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
            if (target == null) return; // only in the saved file (deleted since), or the scene isn't loaded

            _syncingSelection = true;
            try { Selection.activeObject = target; }
            finally { _syncingSelection = false; }
        }

        private void OnEditorSelectionChanged()
        {
            if (_syncingSelection || !_treeBuilt) return;
            var go = Selection.activeGameObject;
            if (go == null || !go.scene.IsValid()) return; // a prefab asset

            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(go);
            var key = (globalId.assetGUID.ToString(), globalId.targetObjectId, globalId.targetPrefabId);
            if (!_treeIdByObject.TryGetValue(key, out var treeId)) return; // not in the saved file yet

            _syncingSelection = true;
            try { _tree.SetSelectionById(treeId); }
            finally { _syncingSelection = false; }
            _tree.ScrollToItemById(treeId);
        }

        // -------------------------------------------------------------------------- table --

        private VisualElement BuildTablePanel()
        {
            var pane = new VisualElement { style = { flexGrow = 1 } };
            pane.Add(UnityGitToolWindow.BuildPanelTitle("Components"));

            _table = new MultiColumnListView
            {
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                // Rows must grow to fit multi-line content (arrays, structs).
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                selectionType = SelectionType.None,
            };
            _table.columns.Add(new Column
            {
                title = "Property", width = 180, minWidth = 90, stretchable = true,
                makeCell = MakePropertyCell,
                bindCell = (element, index) => BindPropertyCell(element, _rows[index]),
            });
            _table.columns.Add(new Column
            {
                title = "Value", width = 320, minWidth = 160, stretchable = true,
                makeCell = MakeValueCell,
                bindCell = (element, index) => BindValueCell(element, _rows[index]),
            });
            _table.AddToClassList("gt-table");
            pane.Add(_table);
            return pane;
        }

        private void ShowRows(SceneYamlNode node)
        {
            _rows = node != null ? SceneYamlTree.BuildRows(node) : new List<SceneYamlRow>();
            if (_table == null) return;
            _table.itemsSource = _rows;
            _table.Rebuild();
        }

        private static VisualElement MakePropertyCell()
        {
            var cell = new VisualElement();
            cell.AddToClassList("gt-cell");
            cell.AddToClassList("gt-property-cell");

            var icon = new Image { scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("gt-tree-icon");
            icon.AddToClassList("gt-icon-sm");
            icon.AddToClassList("gt-icon-leading");
            cell.Add(icon);
            cell.Add(new Label());
            return cell;
        }

        private static void BindPropertyCell(VisualElement element, SceneYamlRow row)
        {
            var icon = (Image)element[0];
            var label = (Label)element[1];
            element.EnableInClassList("gt-row-header", row.IsHeader);
            label.EnableInClassList("gt-row-header-label", row.IsHeader);
            icon.style.display = row.IsHeader ? DisplayStyle.Flex : DisplayStyle.None;
            icon.image = row.IsHeader ? UnityGitToolWindow.GetIcon(row.HeaderIcon) : null;
            label.text = row.Property;
            element.EnableInClassList("gt-row-overridden", row.Overridden);
        }

        private static VisualElement MakeValueCell()
        {
            var cell = new VisualElement();
            cell.AddToClassList("gt-cell");
            cell.AddToClassList("gt-value-cell");
            return cell;
        }

        private static void BindValueCell(VisualElement element, SceneYamlRow row)
        {
            element.Clear(); // cells are recycled across rows, the previous field type may differ
            element.EnableInClassList("gt-row-header", row.IsHeader);
            if (row.Action != null)
            {
                var button = new Button(row.Action) { text = row.ActionLabel, tooltip = row.ActionTooltip };
                button.AddToClassList("gt-pill");
                element.Add(button);
            }
            if (row.IsHeader) return;
            if (row.Value is SerializedProperty property)
            {
                var field = new PropertyField(property, string.Empty);
                field.BindProperty(property);
                field.SetEnabled(false);
                field.AddToClassList("gt-value-field");
                element.Add(field);
            }
            else element.Add(FieldFactory.Create(row.Value));
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
            _cleanAllButton = new Button(CleanAllDeadEntries)
            {
                tooltip = "Remove from the open scenes' files every prefab instance entry whose target no longer exists in its source asset",
                style = { display = DisplayStyle.None, marginLeft = StyleKeyword.Auto },
            };
            _cleanAllButton.AddToClassList("gt-pill");
            footer.Add(_cleanAllButton);
            return footer;
        }
    }
}
