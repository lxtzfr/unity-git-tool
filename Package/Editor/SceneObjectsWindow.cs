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
    /// <summary>The active scene's saved file, read from its YAML like the diff window reads a revision:
    /// GameObject tree on the left, Property | Value of the selected object on the right (read-only).
    /// Selection is synced with the Hierarchy through each object's GlobalObjectId.</summary>
    public class SceneObjectsWindow : EditorWindow
    {
        private TreeView _tree;
        private MultiColumnListView _table;
        private Label _statusLabel;
        private List<SceneYamlRow> _rows = new();
        private YamlHierarchy _hierarchy;
        private string _scenePath;
        private string _sceneGuid;
        private int _gameObjectCount;
        // (targetObjectId, targetPrefabId) of a node's GlobalObjectId -> tree id, for Hierarchy -> tree.
        private readonly Dictionary<(ulong, ulong), int> _treeIdByObject = new();
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
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneDirtied += OnSceneDirtied;
            Selection.selectionChanged += OnEditorSelectionChanged;
        }

        private void OnDisable()
        {
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneDirtied -= OnSceneDirtied;
            Selection.selectionChanged -= OnEditorSelectionChanged;
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

        private void OnActiveSceneChanged(Scene previous, Scene next) => Reload();

        private void OnSceneSaved(Scene scene)
        {
            if (scene.path == _scenePath) Reload();
        }

        private void OnSceneDirtied(Scene scene)
        {
            if (scene.path == _scenePath) UpdateStatus();
        }

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

        /// <summary>Re-reads the active scene's file from disk and rebuilds the tree, keeping what was
        /// expanded (tree ids are rebuilt, so expansion is matched by GlobalObjectId).</summary>
        private void Reload()
        {
            if (_tree == null) return;

            var scene = SceneManager.GetActiveScene();
            _scenePath = scene.path;
            _sceneGuid = AssetDatabase.AssetPathToGUID(_scenePath);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var sources = new YamlHierarchy.SourceCache();
            _hierarchy = YamlHierarchy.Resolve(string.IsNullOrEmpty(_scenePath) || !File.Exists(_scenePath)
                ? new Dictionary<long, GitYamlDocument>()
                : UnityYamlDiffBuilder.IndexByFileId(UnityYamlParser.Parse(File.ReadAllText(_scenePath))), sources);

            var expandedObjects = new HashSet<(ulong, ulong)>();
            if (_treeBuilt)
                foreach (var (key, id) in _treeIdByObject)
                    if (_tree.IsExpanded(id)) expandedObjects.Add(key);

            var nextId = 0;
            var roots = SceneYamlTree.Build(_hierarchy, () => nextId++);
            _treeIdByObject.Clear();
            _gameObjectCount = 0;
            IndexNodes(roots);
            Debug.Log($"[UnityGitTool] Scene Objects: read {_scenePath} + {sources.FilesRead} prefab files, {_gameObjectCount} GameObjects in {watch.ElapsedMilliseconds} ms");

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
                _gameObjectCount++;
                _treeIdByObject.TryAdd(ObjectKey(item.data), item.id);
                IndexNodes(item.children);
            }
        }

        private void UpdateStatus()
        {
            if (_statusLabel == null) return;
            if (string.IsNullOrEmpty(_scenePath)) { _statusLabel.text = "The active scene has never been saved: no file to read."; return; }
            var dirty = SceneManager.GetActiveScene().isDirty ? " — unsaved changes are not shown until the scene is saved" : "";
            _statusLabel.text = $"{_scenePath} — {_gameObjectCount} GameObjects (saved file){dirty}";
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
            return row;
        }

        private static void BindTreeRow(VisualElement element, SceneYamlNode node)
        {
            ((Image)element[0]).image = UnityGitToolWindow.GetIcon(node.Icon);
            ((Label)element[1]).text = node.Label;
        }

        // -------------------------------------------------------------------- selection --

        /// <summary>The (targetObjectId, targetPrefabId) pair Unity's GlobalObjectId gives the loaded
        /// object: the scene fileID for a plain GameObject; for an object inside a prefab instance, its
        /// fileID in the source prefab (nested ones combined), with the scene instance's fileID as prefab id.</summary>
        private static (ulong, ulong) ObjectKey(SceneYamlNode node) =>
            ((ulong)node.GameObject.GlobalObjectId, (ulong)node.GameObject.GlobalPrefabId);

        private void OnTreeSelectionChanged(IEnumerable<object> selection)
        {
            var node = selection.FirstOrDefault() as SceneYamlNode;
            ShowRows(node);
            if (_syncingSelection || node == null || string.IsNullOrEmpty(_sceneGuid)) return;

            var (objectId, prefabId) = ObjectKey(node);
            if (!GlobalObjectId.TryParse($"GlobalObjectId_V1-2-{_sceneGuid}-{objectId}-{prefabId}", out var globalId)) return;
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
            if (go == null || go.scene.path != _scenePath) return; // a prefab asset, another scene

            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(go);
            if (!_treeIdByObject.TryGetValue((globalId.targetObjectId, globalId.targetPrefabId), out var treeId)) return; // not in the saved file yet, or inside a prefab without an override

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
            _rows = node != null ? SceneYamlTree.BuildRows(node, _hierarchy) : new List<SceneYamlRow>();
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
            return footer;
        }
    }
}
