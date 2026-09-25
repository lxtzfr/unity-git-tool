using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>
    /// Diff/merge window, auto-detecting which of two shapes it needs on open (see
    /// <see cref="DetectAndApplyConflictState"/>, no manual toggle):
    /// <list type="bullet">
    /// <item>merge/rebase/cherry-pick/revert stopped on conflicts — header (revision pickers), a left
    /// Files tree (files > GameObjects/documents), the Property | A | Result | B table, and a second
    /// "Result" tree on the right previewing the resolved hierarchy; selecting a node in either tree
    /// selects the same node in the other, both driving the one shared table;</item>
    /// <item>otherwise — a plain two-revision diff: just the Files tree and Property | A | B, nothing
    /// to resolve or preview.</item>
    /// </list>
    /// Visuals come from <see cref="Resources"/>/UnityGitTool.uss. Split into partials by UI section:
    /// <c>.Header</c>, <c>.Tree</c>, <c>.Table</c> — this file keeps the window lifecycle, shared
    /// fields and small cross-section helpers.
    /// </summary>
    public partial class UnityGitToolWindow : EditorWindow
    {
        private MultiColumnListView _table;
        private Column _columnA;
        private Column _columnB;
        private Column _columnResult;
        private List<MockRow> _currentRows = new();
        private MockNode _selectedNode;
        // No manual Diff/Merge toggle anymore — this is set by DetectAndApplyConflictState (see
        // below), which reads whether a real merge/rebase/cherry-pick/revert is actually stopped on
        // conflicts right now (GitFileReader.DetectConflict). True: something to resolve — dual tree,
        // Result column, take-arrows, delete/restore toggles, conflict icons all active. False: a
        // plain two-revision diff with nothing to write back — single tree, Property | A | B only.
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

            DetectAndApplyConflictState(); // sets _isMerge (and A/B when a conflict is found) before anything below reads it

            // Table panel built first so its columns/`_table` exist before either tree panel's
            // initial RefreshTree() populates the table, and before the header wires up the revision
            // pickers that retitle those columns (revision name + Yours/Theirs).
            var tablePanel = BuildTablePanel(_isMerge);
            var treePanel = BuildTreePanel("Files", isResultTree: false, out _tree);

            root.Add(BuildHeader());

            if (_isMerge)
            {
                // Nested two-pane splits for a resizable three-way layout: [main tree | [table | result
                // tree]] — UI Toolkit has no native three-way splitter, but two TwoPaneSplitViews compose
                // into one cleanly. Only worth the extra pane when there's actually a resolution to
                // preview — see BuildTreePanel's isResultTree and BuildTablePanel's isMerge, which strip
                // the Result concept out entirely (not just hide it) in the plain-diff case below.
                var resultTreePanel = BuildTreePanel("Result (preview)", isResultTree: true, out _resultTree);
                RefreshTree(); // both trees exist now — populate them (BuildTreePanel itself no longer does)

                var innerSplit = new TwoPaneSplitView(0, 600, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
                innerSplit.AddToClassList("gt-split");
                innerSplit.Add(tablePanel);
                innerSplit.Add(resultTreePanel);

                var outerSplit = new TwoPaneSplitView(0, 340, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
                outerSplit.AddToClassList("gt-split");
                outerSplit.Add(treePanel);
                outerSplit.Add(innerSplit);
                root.Add(outerSplit);
            }
            else
            {
                _resultTree = null;
                RefreshTree();

                var split = new TwoPaneSplitView(0, 340, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
                split.AddToClassList("gt-split");
                split.Add(treePanel);
                split.Add(tablePanel);
                root.Add(split);
            }

            root.Add(BuildFooter());
        }

        /// <summary>Reads whether a real merge/rebase/cherry-pick/revert is currently stopped on
        /// conflicts (<see cref="GitFileReader.DetectConflict"/>) and sets <see cref="_isMerge"/>
        /// accordingly — no manual toggle anymore (see UnityGitToolWindow.Header.cs's old Diff/Merge
        /// buttons, removed). A conflict in progress points A/B at Working Tree / that operation's
        /// conflict ref (same as the old "Auto-config merge branches" button did); otherwise A/B are
        /// left as whatever they already were (their class-level default, HEAD vs Working Tree, the
        /// first time this runs). Returns whether <see cref="_isMerge"/> actually changed, so a caller
        /// re-detecting later (see the Refresh button) knows whether it needs to rebuild the whole
        /// layout rather than just re-run the diff.</summary>
        private bool DetectAndApplyConflictState()
        {
            var wasMerge = _isMerge;
            var (kind, conflictRef) = GitFileReader.DetectConflict();
            _isMerge = kind != GitConflictKind.None;
            if (_isMerge)
            {
                _revisionA = WorkingTree;
                _revisionB = conflictRef;
            }
            return _isMerge != wasMerge;
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
            _table.itemsSource = _currentRows;
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
