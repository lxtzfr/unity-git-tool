using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityGitTool
{
    internal enum GitChangeStatus
    {
        Added,
        Modified,
        Deleted,
        Renamed,
        Copied,
        Unknown,
    }

    /// <summary>Which git operation (if any) is currently stopped on conflicts — see
    /// <see cref="GitFileReader.DetectConflict"/>.</summary>
    public enum GitConflictKind
    {
        None,
        Merge,
        Rebase,
        CherryPick,
        Revert,
    }

    internal readonly struct GitChangedFile
    {
        public readonly string Path;
        public readonly string OldPath;
        public readonly GitChangeStatus Status;

        public GitChangedFile(string path, string oldPath, GitChangeStatus status)
        {
            Path = path;
            OldPath = oldPath;
            Status = status;
        }
    }

    internal readonly struct GitCommit
    {
        public readonly string Hash;
        public readonly string ShortHash;
        public readonly string Subject;

        public GitCommit(string hash, string shortHash, string subject)
        {
            Hash = hash;
            ShortHash = shortHash;
            Subject = subject;
        }

        public override string ToString() => $"{ShortHash} {Subject}";
    }

    internal readonly struct GitBranch
    {
        public readonly string Name;
        public readonly bool IsCurrent;

        public GitBranch(string name, bool isCurrent)
        {
            Name = name;
            IsCurrent = isCurrent;
        }
    }

    /// <summary>
    /// Thin wrapper around the `git` CLI — lists branches/commits for the revision picker, lists
    /// files changed between two revisions (`git diff --name-status`), and reads a file's content
    /// at a given revision (`git show`, or straight off disk for <see cref="WorkingTree"/>).
    /// Replaces <see cref="MockDataSource"/>'s hardcoded branches/commits; the tree/diff pipeline
    /// (scene/prefab YAML parsing) is still mocked.
    /// </summary>
    internal static class GitFileReader
    {
        /// <summary>Pseudo-revision meaning "the file on disk right now", not a git ref — mirrors
        /// <c>UnityGitToolWindow.WorkingTree</c>, the picker's label for the same concept.</summary>
        public const string WorkingTree = "Working Tree";

        /// <summary>Every git operation that can stop mid-way on conflicts, each backed by its own
        /// pseudo-ref pointing at "the other side" of the 3-way conflict. Order matters: a rebase can
        /// leave stray state from a much older interrupted merge lying around, so the operation-specific
        /// refs are checked before the generic <c>MERGE_HEAD</c>.</summary>
        private static readonly (GitConflictKind Kind, string ConflictRef)[] ConflictRefsByPriority =
        {
            (GitConflictKind.Rebase, "REBASE_HEAD"),
            (GitConflictKind.CherryPick, "CHERRY_PICK_HEAD"),
            (GitConflictKind.Revert, "REVERT_HEAD"),
            (GitConflictKind.Merge, "MERGE_HEAD"),
        };

        /// <summary>Detects whether `git merge`/`rebase`/`cherry-pick`/`revert` is currently stopped on
        /// conflicts, and if so which ref stands in for "the other side". Checked via plumbing
        /// (<c>rev-parse -q --verify</c>) rather than reading <c>.git/MERGE_HEAD</c> etc. directly, so
        /// it still resolves correctly under worktrees/submodules, where <c>.git</c> isn't a plain
        /// directory. Git's own <c>--ours</c>/<c>--theirs</c> convention always means "HEAD" / "this
        /// ref" — for a merge that's intuitive (HEAD = your branch), for a rebase it's the classic
        /// gotcha where HEAD is actually the upstream you're replaying onto and this ref is your own
        /// original commit. This tool follows git's convention rather than inventing its own, so the
        /// "Yours"/"Theirs" column labels stay consistent with `git checkout --ours`/`--theirs`.</summary>
        public static (GitConflictKind Kind, string ConflictRef) DetectConflict()
        {
            foreach (var (kind, conflictRef) in ConflictRefsByPriority)
                if (TryRunGit(RepositoryRoot, $"rev-parse -q --verify {conflictRef}", out _))
                    return (kind, conflictRef);
            return (GitConflictKind.None, null);
        }

        /// <summary>Best-effort display name for <paramref name="gitRef"/> — falls back to its short
        /// hash when it isn't the tip of a known local/remote branch (e.g. merging/rebasing onto a
        /// bare commit or an already-deleted branch).</summary>
        public static string GetRefDisplayName(string gitRef)
        {
            if (TryRunGit(RepositoryRoot, $"name-rev --name-only --exclude=tags/* {gitRef}", out var name))
            {
                name = name.Trim();
                if (!string.IsNullOrEmpty(name) && name != "undefined") return name;
            }
            return TryRunGit(RepositoryRoot, $"rev-parse --short {gitRef}", out var hash) ? hash.Trim() : gitRef;
        }

        private static string _repositoryRoot;

        /// <summary>Absolute path to the repository root, resolved once via
        /// `git rev-parse --show-toplevel` run from <c>Application.dataPath</c> — not just the
        /// Assets folder's parent, so this still works if the Unity project isn't the repo root.</summary>
        public static string RepositoryRoot => _repositoryRoot ??= ResolveRepositoryRoot();

        private static string ResolveRepositoryRoot()
        {
            var assetsParent = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            if (!TryRunGit(assetsParent, "rev-parse --show-toplevel", out var output))
            {
                Debug.LogError("[UnityGitTool] Not inside a git repository.");
                return assetsParent;
            }
            // git always prints posix-style forward slashes, even on Windows.
            return output.Trim().Replace('/', Path.DirectorySeparatorChar);
        }

        public static List<GitBranch> ListBranches()
        {
            var branches = new List<GitBranch>();
            if (!TryRunGit(RepositoryRoot, "branch --format=%(HEAD)|%(refname:short)", out var output))
                return branches;

            foreach (var line in SplitLines(output))
            {
                var parts = line.Split(new[] { '|' }, 2);
                if (parts.Length != 2) continue;
                branches.Add(new GitBranch(parts[1], parts[0] == "*"));
            }
            return branches;
        }

        public static List<GitCommit> ListCommits(string branch, int maxCount = 20)
        {
            var commits = new List<GitCommit>();
            var args = $"log {EscapeArg(branch)} --max-count={maxCount} --pretty=format:%H|%h|%s";
            if (!TryRunGit(RepositoryRoot, args, out var output))
                return commits;

            foreach (var line in SplitLines(output))
            {
                var parts = line.Split(new[] { '|' }, 3);
                if (parts.Length != 3) continue;
                commits.Add(new GitCommit(parts[0], parts[1], parts[2]));
            }
            return commits;
        }

        /// <summary>Exactly the paths git itself considers unresolved right now (index stage &gt; 0 —
        /// the same set `git status` marks UU/AA/AU/UA/UD/DU). A two-revision field diff (see
        /// <see cref="UnityYamlDiffBuilder"/>) has no common ancestor to tell "both sides changed this
        /// field, and it genuinely conflicts" from "both sides differ from some older field value,
        /// but git's real 3-way merge already resolved it cleanly" — every field a rebase/merge
        /// touched at all would otherwise look like an unresolved conflict, on every file it touched,
        /// not just the ones that actually need attention. This is what gates that: a field only ever
        /// counts as a real conflict when its own file is in this set.</summary>
        public static List<string> GetConflictedFiles()
        {
            var files = new List<string>();
            if (!TryRunGit(RepositoryRoot, "diff --name-only --diff-filter=U", out var output))
                return files;
            files.AddRange(SplitLines(output));
            return files;
        }

        /// <summary>Files changed between two revisions. At most one of <paramref name="revisionA"/>
        /// / <paramref name="revisionB"/> may be <see cref="WorkingTree"/> — git has no ref for it,
        /// so that side is simply omitted from the `git diff` call (its default comparison target
        /// when only one revision is given).</summary>
        public static List<GitChangedFile> GetChangedFiles(string revisionA, string revisionB)
        {
            var files = new List<GitChangedFile>();
            var isWorkingTreeA = revisionA == WorkingTree;
            var isWorkingTreeB = revisionB == WorkingTree;
            if (isWorkingTreeA && isWorkingTreeB) return files;

            var args = isWorkingTreeA
                ? $"diff --name-status {EscapeArg(revisionB)}"
                : isWorkingTreeB
                    ? $"diff --name-status {EscapeArg(revisionA)}"
                    : $"diff --name-status {EscapeArg(revisionA)} {EscapeArg(revisionB)}";

            if (!TryRunGit(RepositoryRoot, args, out var output))
                return files;

            foreach (var line in SplitLines(output))
            {
                var fields = line.Split('\t');
                if (fields.Length < 2) continue;

                var status = fields[0][0] switch
                {
                    'A' => GitChangeStatus.Added,
                    'M' => GitChangeStatus.Modified,
                    'D' => GitChangeStatus.Deleted,
                    'R' => GitChangeStatus.Renamed,
                    'C' => GitChangeStatus.Copied,
                    _ => GitChangeStatus.Unknown,
                };

                // Rename/copy lines carry both paths: "R100\told\tnew" — everything else is "X\tpath".
                var isMove = (status == GitChangeStatus.Renamed || status == GitChangeStatus.Copied) && fields.Length >= 3;
                var path = isMove ? fields[2] : fields[1];
                var oldPath = isMove ? fields[1] : null;
                files.Add(new GitChangedFile(path, oldPath, status));
            }

            // A path with unresolved conflict stages in the index can get two lines from `git diff
            // --name-status <tree>` for the exact same resulting path (observed e.g. "M path" then
            // "A path") — git resolving the ambiguous "old side" of an unmerged entry against the
            // tree two different ways rather than a real double change. Keep the first (its status is
            // the meaningful one; a later duplicate is the artifact) so a path is never listed twice.
            var seenPaths = new HashSet<string>();
            return files.Where(f => seenPaths.Add(f.Path)).ToList();
        }

        /// <summary>Stages <paramref name="relativePath"/> (`git add`) — how <see cref="UnityYamlWriter"/>'s
        /// caller marks a conflicted path resolved once it has written the merged content to disk.
        /// Git itself doesn't care that the content still contains an unresolved field; it just cares
        /// that the file was `add`ed, so the caller is responsible for only doing this after a fully
        /// clean apply (no entries in <c>Skipped</c>).</summary>
        public static bool StageFile(string relativePath) =>
            TryRunGit(RepositoryRoot, $"add -- {EscapeArg(relativePath.Replace('\\', '/'))}", out _);

        /// <summary>Content of <paramref name="relativePath"/> at <paramref name="revision"/> — read
        /// straight off disk for <see cref="WorkingTree"/>, `git show revision:path` otherwise.
        /// Returns null if the file doesn't exist on that side (added/removed).</summary>
        public static string ReadFileAtRevision(string revision, string relativePath)
        {
            if (revision == WorkingTree)
            {
                var fullPath = Path.Combine(RepositoryRoot, relativePath);
                return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            }

            var gitPath = relativePath.Replace('\\', '/');
            var args = $"show {EscapeArg($"{revision}:{gitPath}")}";
            return TryRunGit(RepositoryRoot, args, out var output) ? output : null;
        }

        private static IEnumerable<string> SplitLines(string output) =>
            output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

        private static string EscapeArg(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

        /// <summary>Runs `git` with <paramref name="arguments"/> in <paramref name="workingDirectory"/>.
        /// Returns false (and logs stderr) on a non-zero exit code instead of throwing — a failed
        /// lookup (unknown revision, file missing at that revision, no repo here) is an expected,
        /// recoverable outcome for every caller in this class, not a bug.</summary>
        private static bool TryRunGit(string workingDirectory, string arguments, out string output)
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0) return true;
            if (!string.IsNullOrEmpty(error)) Debug.LogWarning($"[UnityGitTool] git {arguments}\n{error}");
            return false;
        }
    }
}
