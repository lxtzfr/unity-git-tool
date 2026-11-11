using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>The active scene laid out like the diff window: GameObject tree on the left,
    /// Property | Value table of the selected GameObject's components on the right, live and editable.</summary>
    public class SceneObjectsWindow : EditorWindow
    {
        private class Row
        {
            public string Property;
            public bool IsHeader;
            public Texture HeaderIcon;
            public SerializedProperty SerializedProperty;
        }

        private TreeView _tree;
        private MultiColumnListView _table;
        private Label _statusLabel;
        private readonly List<Row> _rows = new();
        private GameObject _selected;
        // Our own tree selection pushes Selection.activeGameObject, which fires selectionChanged straight back.
        private bool _syncingSelection;
        // GetTreeCount() / viewController throw until the first SetRootItems.
        private bool _treeBuilt;

        [MenuItem("Unity Git Tool/Scene Objects", priority = 1)]
        private static void Open()
        {
            var window = GetWindow<SceneObjectsWindow>("Scene Objects");
            window.minSize = new Vector2(700, 360);
        }

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += RefreshTree;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            Selection.selectionChanged += OnEditorSelectionChanged;
            ObjectChangeEvents.changesPublished += OnChangesPublished;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= RefreshTree;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            Selection.selectionChanged -= OnEditorSelectionChanged;
            ObjectChangeEvents.changesPublished -= OnChangesPublished;
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

            RefreshTree();
            OnEditorSelectionChanged();
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
                bindItem = (element, index) => BindTreeRow(element, _tree.GetItemDataForIndex<GameObject>(index)),
            };
            _treeBuilt = false;
            _tree.AddToClassList("gt-tree");
            _tree.selectionChanged += OnTreeSelectionChanged;
            pane.Add(_tree);
            return pane;
        }

        private void RefreshTree()
        {
            if (_tree == null) return;

            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects().Select(BuildItem).ToList();
            // Ids are instance ids, stable across rebuilds, so the expanded state survives a hierarchy change.
            var firstBuild = !_treeBuilt;
            _treeBuilt = true;
            var expanded = firstBuild ? new List<int>() : _tree.viewController.GetAllItemIds().Where(_tree.IsExpanded).ToList();

            _tree.SetRootItems(roots);
            _tree.Rebuild();
            if (firstBuild) _tree.ExpandRootItems();
            else foreach (var id in expanded) _tree.ExpandItem(id);

            SetStatus($"{scene.name} — {CountGameObjects(roots)} GameObjects");
            if (_selected == null) ShowComponents(null); // the selected one was destroyed
        }

        private static TreeViewItemData<GameObject> BuildItem(GameObject go)
        {
            var children = new List<TreeViewItemData<GameObject>>(go.transform.childCount);
            foreach (Transform child in go.transform) children.Add(BuildItem(child.gameObject));
            return new TreeViewItemData<GameObject>(go.GetInstanceID(), go, children);
        }

        private static int CountGameObjects(IEnumerable<TreeViewItemData<GameObject>> items) =>
            items.Sum(item => 1 + CountGameObjects(item.children));

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

        private static void BindTreeRow(VisualElement element, GameObject go)
        {
            var icon = (Image)element[0];
            var label = (Label)element[1];
            icon.image = go != null ? HierarchyIcon(go) : null;
            label.text = go != null ? go.name : "(destroyed)";
            label.style.opacity = go != null && go.activeInHierarchy ? 1f : 0.5f;
        }

        /// <summary>The icon the Hierarchy shows. AssetPreview.GetMiniThumbnail gives the prefab icon to
        /// every object inside an instance, children included; the Hierarchy keeps it for the instance
        /// root only, and tells regular / variant / model prefabs apart.</summary>
        private static Texture HierarchyIcon(GameObject go)
        {
            if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                // Typed from the nearest instance's own asset: GetPrefabAssetType(go) answers for the outermost
                // prefab, so an FBX nested in a regular prefab would read as Regular.
                var asset = AssetDatabase.LoadMainAssetAtPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go));
                return UnityGitToolWindow.GetIcon(asset == null ? "Prefab Icon" : PrefabUtility.GetPrefabAssetType(asset) switch
                {
                    PrefabAssetType.Model => "PrefabModel Icon",
                    PrefabAssetType.Variant => "PrefabVariant Icon",
                    _ => "Prefab Icon",
                });
            }
            // An icon picked in the Inspector's icon menu wins over the default cube.
            var custom = EditorGUIUtility.GetIconForObject(go);
            return custom != null ? custom : UnityGitToolWindow.GetIcon("GameObject Icon");
        }

        private void OnTreeSelectionChanged(IEnumerable<object> selection)
        {
            if (_syncingSelection) return;
            var go = selection.FirstOrDefault() as GameObject;
            ShowComponents(go);
            if (go == null) return;

            _syncingSelection = true;
            try { Selection.activeGameObject = go; }
            finally { _syncingSelection = false; }
        }

        private void OnEditorSelectionChanged()
        {
            if (_syncingSelection || !_treeBuilt) return;
            var go = Selection.activeGameObject;
            if (go == null || go.scene != SceneManager.GetActiveScene()) return; // a prefab asset, another scene

            _syncingSelection = true;
            try { _tree.SetSelectionById(go.GetInstanceID()); }
            finally { _syncingSelection = false; }
            _tree.ScrollToItemById(go.GetInstanceID());
            ShowComponents(go);
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            _selected = null;
            RefreshTree();
        }

        private void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            // Component added/removed or a value edited elsewhere (Inspector, undo): rebind the table.
            if (_selected != null) ShowComponents(_selected);
            _tree?.RefreshItems(); // names / active state
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
                itemsSource = _rows,
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

        /// <summary>One header row for the GameObject, then per component a header row followed by its
        /// visible top-level serialized properties (what the Inspector draws, minus the script field).</summary>
        private void ShowComponents(GameObject go)
        {
            _selected = go;
            _rows.Clear();
            if (go != null)
            {
                _rows.Add(new Row { IsHeader = true, Property = go.name, HeaderIcon = HierarchyIcon(go) });
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component == null)
                    {
                        _rows.Add(new Row { IsHeader = true, Property = "(Missing Script)", HeaderIcon = UnityGitToolWindow.GetIcon("console.warnicon") });
                        continue;
                    }

                    _rows.Add(new Row
                    {
                        IsHeader = true,
                        Property = ObjectNames.NicifyVariableName(component.GetType().Name),
                        HeaderIcon = EditorGUIUtility.ObjectContent(component, component.GetType()).image,
                    });

                    var serializedObject = new SerializedObject(component);
                    var property = serializedObject.GetIterator();
                    for (var enterChildren = true; property.NextVisible(enterChildren); enterChildren = false)
                    {
                        if (property.propertyPath == "m_Script") continue;
                        _rows.Add(new Row { Property = property.displayName, SerializedProperty = property.Copy() });
                    }
                }
            }
            _table?.Rebuild();
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

        private static void BindPropertyCell(VisualElement element, Row row)
        {
            var icon = (Image)element[0];
            var label = (Label)element[1];
            element.EnableInClassList("gt-row-header", row.IsHeader);
            label.EnableInClassList("gt-row-header-label", row.IsHeader);
            icon.style.display = row.IsHeader ? DisplayStyle.Flex : DisplayStyle.None;
            icon.image = row.HeaderIcon;
            label.text = row.Property;
        }

        private static VisualElement MakeValueCell()
        {
            var cell = new VisualElement();
            cell.AddToClassList("gt-cell");
            cell.AddToClassList("gt-value-cell");
            return cell;
        }

        private static void BindValueCell(VisualElement element, Row row)
        {
            element.Clear(); // cells are recycled across rows, the previous field type may differ
            element.EnableInClassList("gt-row-header", row.IsHeader);
            if (row.IsHeader) return;
            element.Add(CreateValueField(row.SerializedProperty));
        }

        /// <summary>The diff table's own controls (FieldFactory), written back through the
        /// SerializedProperty (undoable). Object references bind an ObjectField (FieldFactory leaves a
        /// null one empty); arrays, structs and managed references fall back to an unlabeled PropertyField.</summary>
        private static VisualElement CreateValueField(SerializedProperty property)
        {
            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                var objectField = new ObjectField { allowSceneObjects = true };
                objectField.AddToClassList("gt-value-field");
                objectField.BindProperty(property);
                return objectField;
            }

            var isPlainValue = property.propertyType is not (SerializedPropertyType.Generic or SerializedPropertyType.ManagedReference)
                               && (!property.isArray || property.propertyType == SerializedPropertyType.String);
            object value = null;
            if (isPlainValue)
            {
                try { value = property.boxedValue; }
                catch (System.Exception) { /* a type boxedValue can't read: PropertyField below */ }
            }
            if (value != null)
            {
                return FieldFactory.Create(value, enabled: property.editable, onValueChanged: v =>
                {
                    property.serializedObject.Update();
                    property.boxedValue = ConvertBack(property, v);
                    property.serializedObject.ApplyModifiedProperties();
                });
            }

            var field = new PropertyField(property, string.Empty);
            field.AddToClassList("gt-value-field");
            field.Bind(property.serializedObject);
            return field;
        }

        /// <summary>FieldFactory shows a Quaternion as euler angles; map that edit back to the property's own type.</summary>
        private static object ConvertBack(SerializedProperty property, object value) =>
            property.propertyType == SerializedPropertyType.Quaternion && value is Vector3 euler ? Quaternion.Euler(euler) : value;

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

        private void SetStatus(string message)
        {
            if (_statusLabel != null) _statusLabel.text = message;
        }
    }
}
