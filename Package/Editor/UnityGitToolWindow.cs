using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>
    /// Mock-only shell for the diff/merge window: header (revision pickers + filters), a left
    /// tree (files > GameObjects/documents > components) and a right property table
    /// (Property | A | B | Result), with a status bar. All data here is hardcoded
    /// (<see cref="MockDataSource"/>) — this is purely to validate the layout before the real
    /// git/parse/diff pipeline is wired in. Visuals come from <see cref="Resources"/>/UnityGitTool.uss.
    /// Split into partials by UI section: <c>.Header</c>, <c>.Tree</c>, <c>.Table</c> — this file
    /// keeps the window lifecycle, shared fields and small cross-section helpers.
    /// </summary>
    public partial class UnityGitToolWindow : EditorWindow
    {
        private MultiColumnListView _table;
        private Column _columnA;
        private Column _columnB;
        private Column _columnResult;
        private Column _columnActions;
        private List<MockRow> _currentRows = new();
        private bool _onlyConflicts;
        // Diff vs Merge (see Header.cs's SetMode). A conflict warning only means something once
        // there's a Result to pick — in plain Diff mode every differing field would show one (no
        // 3-way base to tell an auto-mergeable change from a real conflict, see UnityYamlDiffBuilder),
        // which reads as noise rather than a signal. Tree/table icons check this before showing.
        private bool _isMerge;
        private Label _statusLabel;

        private const string WorkingTree = GitFileReader.WorkingTree;
        private string _revisionA = "HEAD";
        private string _revisionB = WorkingTree;

        private static readonly Color BadgeGreen = new(0.35f, 0.75f, 0.4f);
        private static readonly Color BadgeOrange = new(0.95f, 0.62f, 0.15f);
        private static readonly Color BadgeRed = new(0.85f, 0.35f, 0.3f);

        [MenuItem("Window/Unity Git Tool")]
        private static void Open()
        {
            var window = GetWindow<UnityGitToolWindow>("Unity Git Tool");
            window.minSize = new Vector2(900, 480);
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            var stylesheet = Resources.Load<StyleSheet>("UnityGitTool");
            if (stylesheet != null) root.styleSheets.Add(stylesheet);

            // Table panel built first so its columns/`_table` exist before the tree panel's initial
            // RefreshTree() populates the table, and before the header wires up the revision pickers
            // that retitle those columns (revision name + Yours/Theirs).
            var tablePanel = BuildTablePanel();
            var treePanel = BuildTreePanel();

            root.Add(BuildHeader());

            var split = new TwoPaneSplitView(0, 260, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            split.AddToClassList("gt-split");
            split.Add(treePanel);
            split.Add(tablePanel);
            root.Add(split);

            root.Add(BuildFooter());
        }

        // ------------------------------------------------------------------------- shared --

        private static VisualElement BuildPanelTitle(string title)
        {
            var container = new VisualElement();
            container.AddToClassList("gt-panel-title");
            container.Add(new Label(title));
            return container;
        }

        private VisualElement BuildFooter()
        {
            var footer = new VisualElement();
            footer.AddToClassList("gt-footer");
            var infoIcon = new Image { image = GetIcon("console.infoicon"), scaleMode = ScaleMode.ScaleToFit };
            infoIcon.AddToClassList("gt-tree-icon");
            footer.Add(infoIcon);
            _statusLabel = new Label("Mock data — no git wired in yet.");
            footer.Add(_statusLabel);
            return footer;
        }

        private void SetStatus(string message) => _statusLabel.text = message;

        private void RefreshTable()
        {
            var visible = _onlyConflicts ? _currentRows.Where(r => r.IsConflict).ToList() : _currentRows;
            _table.itemsSource = visible;
            _table.Rebuild();
        }

        private static Texture2D GetIcon(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var themed = EditorGUIUtility.isProSkin ? "d_" + name : name;
            var content = EditorGUIUtility.IconContent(themed);
            if (content?.image == null) content = EditorGUIUtility.IconContent(name);
            return content?.image as Texture2D;
        }
    }
}
