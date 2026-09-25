using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Left panel: the files > GameObjects/documents > components tree.</summary>
    public partial class UnityGitToolWindow
    {
        private VisualElement BuildTreePanel()
        {
            var pane = new VisualElement { style = { flexGrow = 1 } };
            pane.Add(BuildPanelTitle("Files"));

            var tree = new TreeView
            {
                fixedItemHeight = 24,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                makeItem = BuildTreeRow,
            };
            tree.AddToClassList("gt-tree");
            tree.bindItem = (element, index) =>
            {
                var node = tree.GetItemDataForIndex<MockNode>(index);
                BindTreeRow(element, node);
            };
            tree.SetRootItems(MockDataSource.BuildTree());
            tree.selectionChanged += selection =>
            {
                if (selection.FirstOrDefault() is MockNode node && MockDataSource.RowsByNodeId.TryGetValue(node.Id, out var rows))
                {
                    _currentRows = rows;
                    RefreshTable();
                }
            };
            tree.ExpandRootItems();
            pane.Add(tree);
            return pane;
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
