using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Toolbar: revision pickers (A/B), manual refresh, Diff/Merge mode, and filter pills.</summary>
    public partial class UnityGitToolWindow
    {
        private VisualElement BuildHeader()
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("gt-toolbar");

            var revisionGroup = new VisualElement();
            revisionGroup.AddToClassList("gt-filter-group");

            void RunDiff() => SetStatus($"Compared {_revisionA} vs {_revisionB} (mock)");

            // A/B become editable when that side IS the working tree — it's the only side backed
            // by a real mutable file, unlike a commit which is frozen history. Changing which side
            // points at it needs a full RefreshItems so the columns re-bind with the right enabled
            // state.
            var menuA = BuildRevisionMenu(_revisionA, v =>
            {
                _revisionA = v;
                UpdateColumnHeaders(_revisionA, _revisionB);
                RunDiff();
                RefreshTree();
                _table.RefreshItems();
            });
            var menuB = BuildRevisionMenu(_revisionB, v =>
            {
                _revisionB = v;
                UpdateColumnHeaders(_revisionA, _revisionB);
                RunDiff();
                RefreshTree();
                _table.RefreshItems();
            });
            menuA.AddToClassList("gt-revision-menu-first");
            revisionGroup.Add(menuA);
            revisionGroup.Add(menuB);
            UpdateColumnHeaders(_revisionA, _revisionB);

            // The diff already re-runs automatically when A or B changes (see BuildRevisionMenu) —
            // this is only for re-syncing against the *same* selection, e.g. new commits landed on
            // the branch you're already comparing against.
            var refresh = new Button(() =>
            {
                RunDiff();
                RefreshTree();
            })
            { text = "Refresh" };
            refresh.AddToClassList("gt-pill");
            refresh.style.marginRight = 8;
            refresh.tooltip = "Re-run the diff without changing the selection";

            var modeGroup = new VisualElement();
            modeGroup.AddToClassList("gt-filter-group");
            Button diffModeButton = null;
            Button mergeModeButton = null;
            diffModeButton = new Button(() => SetMode(isMerge: false)) { text = "Diff" };
            mergeModeButton = new Button(() => SetMode(isMerge: true)) { text = "Merge" };
            diffModeButton.AddToClassList("gt-pill");
            mergeModeButton.AddToClassList("gt-pill");
            modeGroup.Add(diffModeButton);
            modeGroup.Add(mergeModeButton);

            // Manual for now — a real merge in progress (.git/MERGE_HEAD present) should default
            // this to Merge automatically later, with these two buttons staying as the override for
            // previewing a hypothetical merge without actually being mid-merge.
            void SetMode(bool isMerge)
            {
                _isMerge = isMerge;
                _columnResult.visible = isMerge;
                _columnActions.visible = isMerge;
                diffModeButton.EnableInClassList("gt-toggle-chip-active", !isMerge);
                mergeModeButton.EnableInClassList("gt-toggle-chip-active", isMerge);
                // Conflict warning icons (property rows, tree nodes) only mean something in Merge
                // mode — re-bind visible items so they pick up the new _isMerge value immediately.
                _table.RefreshItems();
                _tree.RefreshItems();
            }
            SetMode(isMerge: false);

            var filterGroup = new VisualElement();
            filterGroup.AddToClassList("gt-filter-group");

            var onlyChanges = BuildToggleChip("Only changes", false, _ => { });
            var onlyConflicts = BuildToggleChip("Only conflicts", false, v =>
            {
                _onlyConflicts = v;
                RefreshTable();
            });
            var hideIncompatible = BuildToggleChip("Hide incompatible files", true, _ => { });
            filterGroup.Add(onlyChanges);
            filterGroup.Add(onlyConflicts);
            filterGroup.Add(hideIncompatible);

            toolbar.Add(revisionGroup);
            toolbar.Add(refresh);
            toolbar.Add(modeGroup);
            toolbar.Add(filterGroup);
            var spacer = new VisualElement();
            spacer.AddToClassList("gt-flex-spacer");
            toolbar.Add(spacer);
            return toolbar;
        }

        /// <summary>HEAD / Working Tree pinned at top, then one submenu per branch containing that
        /// branch's tip plus its recent commits — mirrors how a real git revision picker nests
        /// commits under the branch they belong to, instead of one unrelated flat commit list.
        /// The submenu label carries the commit's subject for readability, but the value passed to
        /// <paramref name="onChange"/> (and shown once selected) is always a real git revision —
        /// the branch name or the commit hash — never that display text.</summary>
        private static ToolbarMenu BuildRevisionMenu(string initialValue, System.Action<string> onChange)
        {
            var menu = new ToolbarMenu { text = initialValue };
            menu.AddToClassList("gt-revision-menu");

            void Select(string value)
            {
                menu.text = value;
                onChange(value);
            }

            menu.menu.AppendAction("HEAD", _ => Select("HEAD"));
            menu.menu.AppendAction(GitFileReader.WorkingTree, _ => Select(GitFileReader.WorkingTree));
            menu.menu.AppendSeparator("");
            foreach (var branch in GitFileReader.ListBranches())
            {
                var branchName = branch.Name;
                menu.menu.AppendAction($"{branchName}/(tip)", _ => Select(branchName));
                foreach (var commit in GitFileReader.ListCommits(branchName))
                {
                    // The short hash is what gets displayed (as the menu's own text, and baked into
                    // the A/B column titles) — the full 40-char SHA is long enough to wrap those and
                    // grow the header's height. Git accepts either for every command this tool runs.
                    var hash = commit.ShortHash;
                    menu.menu.AppendAction($"{branchName}/{commit}", _ => Select(hash));
                }
            }
            return menu;
        }

        /// <summary>A filter toggle styled as a pill that fills with color when active, instead of
        /// the default checkbox-style <see cref="Toggle"/> — easier to scan at a glance in a row of
        /// several filters.</summary>
        private static Button BuildToggleChip(string label, bool initialValue, System.Action<bool> onChange)
        {
            var isOn = initialValue;
            var button = new Button { text = label };
            button.AddToClassList("gt-pill");
            button.EnableInClassList("gt-toggle-chip-active", isOn);
            button.clicked += () =>
            {
                isOn = !isOn;
                button.EnableInClassList("gt-toggle-chip-active", isOn);
                onChange(isOn);
            };
            return button;
        }

        private void UpdateColumnHeaders(string revisionA, string revisionB)
        {
            if (_columnA == null || _columnB == null) return;
            _columnA.title = $"{revisionA} (Yours)";
            _columnB.title = $"{revisionB} (Theirs)";
        }
    }
}
