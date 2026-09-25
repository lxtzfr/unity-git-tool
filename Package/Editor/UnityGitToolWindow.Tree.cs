using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Left panel: the files > GameObjects/documents > components tree.</summary>
    public partial class UnityGitToolWindow
    {
        private TreeView _tree;

        private VisualElement BuildTreePanel()
        {
            var pane = new VisualElement { style = { flexGrow = 1 } };
            pane.Add(BuildPanelTitle("Files"));

            _tree = new TreeView
            {
                fixedItemHeight = 24,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                makeItem = BuildTreeRow,
            };
            _tree.AddToClassList("gt-tree");
            _tree.bindItem = (element, index) =>
            {
                var node = _tree.GetItemDataForIndex<MockNode>(index);
                BindTreeRow(element, node);
            };
            _tree.selectionChanged += selection =>
            {
                if (selection.FirstOrDefault() is MockNode node && MockDataSource.RowsByNodeId.TryGetValue(node.Id, out var rows))
                {
                    _currentRows = rows;
                    RefreshTable();
                }
            };
            RefreshTree();
            pane.Add(_tree);
            return pane;
        }

        /// <summary>Rebuilds the left tree from the files that actually changed between
        /// <see cref="_revisionA"/> and <see cref="_revisionB"/> (<see cref="GitFileReader.GetChangedFiles"/>),
        /// filtered to scene/prefab files — the only ones this tool knows how to open. Each file is
        /// currently a leaf with no GameObject/component drill-down and an empty row list: that needs
        /// a YAML diff parser for Unity's scene/prefab format, still to come.</summary>
        private void RefreshTree()
        {
            MockDataSource.RowsByNodeId.Clear();
            _currentRows = new List<MockRow>();

            var nextId = 0;
            int NextId() => nextId++;

            var roots = new List<TreeViewItemData<MockNode>>();
            foreach (var file in GitFileReader.GetChangedFiles(_revisionA, _revisionB))
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
                // A renamed file's old path only exists on the A side — GetChangedFiles already
                // reports the new path for B, so read A back at its OldPath when there is one.
                var contentA = GitFileReader.ReadFileAtRevision(_revisionA, file.OldPath ?? file.Path);
                var contentB = GitFileReader.ReadFileAtRevision(_revisionB, file.Path);

                roots.Add(UnityYamlDiffBuilder.BuildFileNode(
                    NextId, MockDataSource.RowsByNodeId, Path.GetFileName(file.Path), kind, badge, contentA, contentB));
            }

            _tree.SetRootItems(roots);
            _tree.Rebuild();
            _tree.ExpandRootItems();
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

        private static void BindTreeRow(VisualElement element, MockNode node)
        {
            var typeIcon = (Image)element[0];
            var label = (Label)element[1];
            var conflictIcon = (Image)element[2];
            var badgeLabel = (Label)element[3];

            typeIcon.image = GetIcon(node.Kind switch
            {
                MockNodeKind.SceneFile => "SceneAsset Icon",
                MockNodeKind.PrefabFile => "Prefab Icon",
                MockNodeKind.GameObject => "GameObject Icon",
                MockNodeKind.ComponentTransform => "Transform Icon",
                MockNodeKind.ComponentMeshRenderer => "MeshRenderer Icon",
                _ => "cs Script Icon",
            });
            label.text = node.Label;
            conflictIcon.style.display = node.HasConflict ? DisplayStyle.Flex : DisplayStyle.None;

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
    }
}
