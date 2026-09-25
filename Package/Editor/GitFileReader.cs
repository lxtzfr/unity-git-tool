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
            return files;
        }

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
