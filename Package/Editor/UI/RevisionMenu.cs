using System.Collections.Generic;
using System.Linq;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using UnityGitTool.Git;

namespace UnityGitTool
{
    /// <summary>
    /// Toolbar revision selector: a branch dropdown next to a commit dropdown, rather than one
    /// menu mixing both. Picking a branch scopes the commit dropdown to that branch's own
    /// history (<c>git log &lt;branch&gt;</c>) instead of always listing HEAD's last commits, and
    /// sets <see cref="Value"/> to the branch name (diff against its tip). Picking a commit pins
    /// <see cref="Value"/> to that exact commit, taking precedence until a branch is picked again.
    /// </summary>
    internal class RevisionMenu : VisualElement
    {
        private const int RecentCommitCount = 20;

        public string Value { get; private set; }

        private readonly string _label;
        private readonly string _repoRoot;
        private readonly List<string> _branches;
        private readonly ToolbarMenu _branchMenu;
        private readonly ToolbarMenu _commitMenu;

        private string _selectedBranch;
        private List<GitFileReader.CommitInfo> _commits;

        public RevisionMenu(string label, string initialValue, List<string> branches, List<GitFileReader.CommitInfo> initialCommits, string repoRoot)
        {
            _label = label;
            _repoRoot = repoRoot;
            _branches = branches;
            _commits = initialCommits;
            Value = initialValue;

            style.flexDirection = FlexDirection.Row;

            _branchMenu = new ToolbarMenu { style = { width = 110 } };
            foreach (var branch in _branches)
            {
                _branchMenu.menu.AppendAction(branch, _ => SelectBranch(branch),
                    _ => _selectedBranch == branch ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
            Add(_branchMenu);

            _commitMenu = new ToolbarMenu { style = { width = 170, marginLeft = 2 } };
            Add(_commitMenu);
            RebuildCommitMenu();

            RefreshText();
        }

        private void SelectWorkingTree()
        {
            _selectedBranch = null;
            Value = GitFileReader.WorkingTreeRevision;
            RefreshText();
        }

        public void SetValue(string value)
        {
            Value = value;
            _selectedBranch = _branches.FirstOrDefault(b => b == value);
            if (_selectedBranch != null)
            {
                RefreshCommitsForBranch(_selectedBranch);
            }
            RefreshText();
        }

        private void SelectBranch(string branch)
        {
            _selectedBranch = branch;
            Value = branch;
            RefreshCommitsForBranch(branch);
            RefreshText();
        }

        private void RefreshCommitsForBranch(string branch)
        {
            if (GitFileReader.TryListRecentCommits(_repoRoot, RecentCommitCount, out var commits, out _, branch))
            {
                _commits = commits;
                RebuildCommitMenu();
            }
        }

        private void RebuildCommitMenu()
        {
            _commitMenu.menu.MenuItems().Clear();
            _commitMenu.menu.AppendAction("Working tree (uncommitted changes)", _ => SelectWorkingTree(),
                _ => GitFileReader.IsWorkingTree(Value) ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            _commitMenu.menu.AppendSeparator();
            foreach (var commit in _commits)
            {
                _commitMenu.menu.AppendAction($"{commit.Hash}  {commit.Subject}", _ => SelectCommit(commit.Hash),
                    _ => Value == commit.Hash ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
        }

        private void SelectCommit(string hash)
        {
            Value = hash;
            RefreshText();
        }

        private void RefreshText()
        {
            _branchMenu.text = $"{_label}: {_selectedBranch ?? "Branch"}";

            if (GitFileReader.IsWorkingTree(Value))
            {
                _commitMenu.text = "Working Tree";
                return;
            }

            var commit = _commits.FirstOrDefault(c => c.Hash == Value);
            _commitMenu.text = commit.Hash != null ? $"{commit.Hash}  {commit.Subject}" : Value;
        }
    }
}
