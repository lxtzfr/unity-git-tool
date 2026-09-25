using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Toolbar: revision pickers (A/B), manual refresh, Diff/Merge mode.</summary>
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

            // Manual by default; "Auto-config merge branches" below flips this to Merge automatically
            // when a real merge is in progress. These two buttons stay as the override for previewing
            // a hypothetical merge (any A/B pair) without actually being mid-merge.
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

            // One-click stand-in for "config auto les branches en merge conflict": detects whichever
            // of merge/rebase/cherry-pick/revert is stopped on conflicts and points A/B at Working Tree
            // (yours) / that operation's conflict ref (theirs) — real refs, so
            // GetChangedFiles/ReadFileAtRevision need no special-casing — then flips to Merge mode.
            var autoConfig = new Button(() =>
            {
                var (kind, conflictRef) = GitFileReader.DetectConflict();
                if (kind == GitConflictKind.None)
                {
                    SetStatus("No merge/rebase/cherry-pick/revert in progress — nothing to auto-configure.");
                    return;
                }
                // Working Tree rather than "HEAD" for A: byte-identical to HEAD right now (git leaves
                // the working copy untouched until something resolves the conflict), but Working Tree
                // is what ApplyResolution actually reads/patches/writes — using it here means the tool
                // is already pointed at a file Apply can act on, no extra step needed.
                _revisionA = WorkingTree;
                _revisionB = conflictRef;
                menuA.text = _revisionA;
                menuB.text = _revisionB;
                UpdateColumnHeaders(_revisionA, _revisionB);
                SetMode(isMerge: true);
                SetStatus($"{kind} in progress: Working Tree vs {GitFileReader.GetRefDisplayName(conflictRef)} ({conflictRef})");
                RunDiff();
                RefreshTree();
                _table.RefreshItems();
            })
            { text = "Auto-config merge branches" };
            autoConfig.AddToClassList("gt-pill");
            autoConfig.style.marginRight = 8;
            autoConfig.tooltip = "Detect an in-progress merge/rebase/cherry-pick/revert and set A/B to HEAD / the conflict ref";

            // Writes the selected file's resolved fields back to disk and stages it — see
            // UnityGitToolWindow.Apply.cs. Only meaningful once A is Working Tree (Auto-config above
            // sets it) and a whole file (not just one GameObject) is selected in the tree.
            var apply = new Button(ApplyResolution) { text = "Apply resolution" };
            apply.AddToClassList("gt-pill");
            apply.style.marginRight = 8;
            apply.tooltip = "Write the selected file's resolved fields to the working tree and `git add` it";

            toolbar.Add(revisionGroup);
            toolbar.Add(refresh);
            toolbar.Add(autoConfig);
            toolbar.Add(apply);
            toolbar.Add(modeGroup);
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

        private void UpdateColumnHeaders(string revisionA, string revisionB)
        {
            if (_columnA == null || _columnB == null) return;
            _columnA.title = $"{revisionA} (Yours)";
            _columnB.title = $"{revisionB} (Theirs)";
        }
    }
}
