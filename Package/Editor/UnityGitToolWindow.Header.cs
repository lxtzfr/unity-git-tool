using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>Toolbar: revision pickers (A/B), manual refresh.</summary>
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
            // this is mainly for re-syncing against the *same* selection (e.g. new commits landed on
            // the branch you're already comparing against), but it also re-checks whether a
            // merge/rebase/cherry-pick/revert started or finished outside this tool since it opened
            // (see DetectAndApplyConflictState) — if that flips _isMerge, the whole layout (single vs
            // dual tree, Result column or not — see CreateGUI) needs rebuilding, not just a data refresh.
            var refresh = new Button(() =>
            {
                if (DetectAndApplyConflictState())
                {
                    rootVisualElement.Clear();
                    CreateGUI();
                    return;
                }
                RunDiff();
                RefreshTree();
            })
            { text = "Refresh" };
            refresh.AddToClassList("gt-pill");
            refresh.tooltip = "Re-run the diff without changing the selection";

            toolbar.Add(revisionGroup);
            toolbar.Add(refresh);
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

        /// <summary>"(Yours)"/"(Theirs)" is git's own merge-conflict convention (<c>git checkout
        /// --ours/--theirs</c>) — meaningful only in Merge mode, where A/B really are the two sides of
        /// a real conflict (see DetectAndApplyConflictState). Outside it, A/B are just two arbitrary
        /// revisions the user picked to browse — labeling them "Yours"/"Theirs" would claim a
        /// relationship that isn't there, so the column just shows the revision name.</summary>
        private void UpdateColumnHeaders(string revisionA, string revisionB)
        {
            if (_columnA == null || _columnB == null) return;
            _columnA.title = _isMerge ? $"{revisionA} (Yours)" : revisionA;
            _columnB.title = _isMerge ? $"{revisionB} (Theirs)" : revisionB;
        }
    }
}
