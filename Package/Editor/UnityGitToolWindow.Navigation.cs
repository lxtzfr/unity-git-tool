using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Files tree navigation: the "Changed only" view (each file lists just its changed
    /// objects, flat, their parent path dimmed beside the name) and the table's breadcrumb (file >
    /// ancestors > selected object, each segment selects that node).</summary>
    public partial class UnityGitToolWindow
    {
        private const string ChangedOnlyPref = "UnityGitTool.ChangedOnly";

        private bool _changedOnly;
        private readonly Dictionary<int, MockNode> _parentById = new();
        private readonly HashSet<int> _displayedIds = new();
        private VisualElement _breadcrumb;

        private Toggle BuildChangedOnlyToggle()
        {
            _changedOnly = EditorPrefs.GetBool(ChangedOnlyPref, false);
            var toggle = new Toggle("Changed only") { value = _changedOnly, tooltip = "List only the changed objects of each file, without their unchanged parents" };
            toggle.AddToClassList("gt-title-toggle");
            toggle.RegisterValueChangedCallback(e =>
            {
                _changedOnly = e.newValue;
                EditorPrefs.SetBool(ChangedOnlyPref, _changedOnly);
                Debug.Log($"[UnityGitTool] Files tree: changed only = {_changedOnly}");
                ApplyRoots(CaptureTreeState());
            });
            return toggle;
        }

        /// <summary>A node the table has something to show for: a changed object, not a structural
        /// parent (Badge None) nor a prefab instance whose changes all sit on its object children.</summary>
        private static bool IsChangedNode(MockNode node) =>
            node.Badge != MockBadge.None &&
            (node.Kind == MockNodeKind.GameObject || MockDataSource.OwnRowsByNodeId.GetValueOrDefault(node.Id)?.Count > 0);

        /// <summary>What the trees bind: <see cref="_roots"/>, or in "Changed only" each file with its
        /// changed descendants flattened under it. Same ids either way, so selection and marks carry over.</summary>
        private List<TreeViewItemData<MockNode>> DisplayedRoots()
        {
            _parentById.Clear();
            void IndexParents(IEnumerable<TreeViewItemData<MockNode>> items, MockNode parent)
            {
                foreach (var item in items)
                {
                    if (parent != null) _parentById[item.id] = parent;
                    IndexParents(item.children, item.data);
                }
            }
            IndexParents(_roots, null);

            var displayed = !_changedOnly
                ? _roots
                : _roots.Select(file => new TreeViewItemData<MockNode>(file.id, file.data, ChangedDescendants(file.children).ToList())).ToList();

            _displayedIds.Clear();
            void IndexDisplayed(IEnumerable<TreeViewItemData<MockNode>> items)
            {
                foreach (var item in items) { _displayedIds.Add(item.id); IndexDisplayed(item.children); }
            }
            IndexDisplayed(displayed);
            return displayed;
        }

        private static IEnumerable<TreeViewItemData<MockNode>> ChangedDescendants(IEnumerable<TreeViewItemData<MockNode>> items)
        {
            foreach (var item in items)
            {
                if (IsChangedNode(item.data)) yield return new TreeViewItemData<MockNode>(item.id, item.data);
                foreach (var descendant in ChangedDescendants(item.children)) yield return descendant;
            }
        }

        /// <summary>The file node first, the node itself last.</summary>
        private List<MockNode> PathTo(MockNode node)
        {
            var path = new List<MockNode>();
            for (var current = node; current != null; current = _parentById.GetValueOrDefault(current.Id)) path.Add(current);
            path.Reverse();
            return path;
        }

        /// <summary>"Changed only" row suffix: the ancestors between the file and the node.</summary>
        private string ParentPathLabel(MockNode node)
        {
            var path = PathTo(node);
            return path.Count > 2 ? string.Join(" / ", path.Skip(1).Take(path.Count - 2).Select(n => n.Label)) : "";
        }

        private VisualElement BuildBreadcrumb()
        {
            _breadcrumb = new VisualElement();
            _breadcrumb.AddToClassList("gt-breadcrumb");
            return _breadcrumb;
        }

        private void UpdateBreadcrumb()
        {
            if (_breadcrumb == null) return;
            _breadcrumb.Clear();
            _breadcrumb.style.display = _selectedNode != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (_selectedNode == null) return;

            var path = PathTo(_selectedNode);
            for (var i = 0; i < path.Count; i++)
            {
                var node = path[i];
                if (i > 0) _breadcrumb.Add(new Label(">") { pickingMode = PickingMode.Ignore });

                var segment = new Button(() => SelectNode(node)) { text = node.Label, tooltip = node.FilePath };
                segment.AddToClassList("gt-breadcrumb-segment");
                segment.EnableInClassList("gt-breadcrumb-current", node == _selectedNode);
                // A structural parent hidden by "Changed only" has no row to select.
                segment.SetEnabled(node != _selectedNode && _displayedIds.Contains(node.Id));
                _breadcrumb.Add(segment);
            }
        }

        private void SelectNode(MockNode node)
        {
            _tree.SetSelectionById(node.Id);
            _tree.ScrollToItemById(node.Id);
        }
    }
}
