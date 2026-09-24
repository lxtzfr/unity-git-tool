using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UnityGitTool.Git
{
    /// <summary>
    /// Reads the content of a file at a given git revision without touching the working tree.
    /// </summary>
    public static class GitFileReader
    {
        /// <summary>Sentinel <c>RevisionMenu.Value</c> meaning "the working tree as it is on
        /// disk right now" (staged + unstaged + untracked), rather than a git-resolvable
        /// revision. Never a valid branch/commit name, so it's safe to compare by value.</summary>
        public const string WorkingTreeRevision = "(working tree)";

        public static bool IsWorkingTree(string revision) => revision == WorkingTreeRevision;

        public readonly struct ChangedFile
        {
            public readonly string Path;
            public readonly string Status; // "A", "M", "D", "R", ...

            public ChangedFile(string path, string status)
            {
                Path = path;
                Status = status;
            }
        }

        public readonly struct CommitInfo
        {
            public readonly string Hash;
            public readonly string Subject;

            public CommitInfo(string hash, string subject)
            {
                Hash = hash;
                Subject = subject;
            }
        }

        private static bool RunGit(string repositoryPath, IEnumerable<string> args, out string output, out string error)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            output = process.StandardOutput.ReadToEnd();
            error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0;
        }

        /// <summary>Lists paths that differ between two revisions (relative to the repo root).</summary>
        public static bool TryListChangedFiles(
            string repositoryPath,
            string revisionA,
            string revisionB,
            out List<string> paths,
            out string error)
        {
            if (!RunGit(repositoryPath, new[] { "diff", "--name-only", revisionA, revisionB }, out var output, out error))
            {
                paths = null;
                return false;
            }

            paths = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    paths.Add(trimmed);
                }
            }

            return true;
        }

        /// <summary>Lists changed paths between two revisions along with their git status
        /// letter (A/M/D/R...), relative to the repo root. Either side may be
        /// <see cref="WorkingTreeRevision"/> to compare against the working tree as it is on
        /// disk right now (staged + unstaged + untracked) instead of a committed revision.</summary>
        public static bool TryListChangedFilesWithStatus(
            string repositoryPath,
            string revisionA,
            string revisionB,
            out List<ChangedFile> files,
            out string error)
        {
            var aIsWorkingTree = IsWorkingTree(revisionA);
            var bIsWorkingTree = IsWorkingTree(revisionB);

            if (aIsWorkingTree && bIsWorkingTree)
            {
                files = new List<ChangedFile>();
                error = null;
                return true;
            }

            // `git diff --name-status <rev>` (no second revision) already compares <rev> against
            // the working tree, combining staged and unstaged changes. Whichever side is the
            // committed revision goes first so the status letters keep their usual meaning; if
            // it's revisionA that's swapped out, the comparison direction is reversed afterwards.
            var invertStatus = aIsWorkingTree;
            var args = bIsWorkingTree || aIsWorkingTree
                ? new[] { "diff", "--name-status", invertStatus ? revisionB : revisionA }
                : new[] { "diff", "--name-status", revisionA, revisionB };

            if (!RunGit(repositoryPath, args, out var output, out error))
            {
                files = null;
                return false;
            }

            files = new List<ChangedFile>();
            var seenPaths = new HashSet<string>();
            foreach (var line in output.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;

                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                var status = parts[0].Length > 0 ? parts[0].Substring(0, 1) : "M";
                // Renames/copies report "old\tnew" — the new path is what exists at revisionB.
                var path = parts[^1];
                files.Add(new ChangedFile(path, invertStatus ? InvertStatus(status) : status));
                seenPaths.Add(path);
            }

            // `git diff` never reports untracked files even against the working tree — surface
            // them too (as "Added") since they're very much part of "what's changed right now".
            if (bIsWorkingTree && TryListUntrackedFiles(repositoryPath, out var untracked, out _))
            {
                foreach (var path in untracked)
                {
                    if (seenPaths.Add(path)) files.Add(new ChangedFile(path, "A"));
                }
            }
            else if (aIsWorkingTree && TryListUntrackedFiles(repositoryPath, out var untrackedRev, out _))
            {
                foreach (var path in untrackedRev)
                {
                    if (seenPaths.Add(path)) files.Add(new ChangedFile(path, "D"));
                }
            }

            return true;
        }

        private static string InvertStatus(string status) => status switch
        {
            "A" => "D",
            "D" => "A",
            _ => status,
        };

        private static bool TryListUntrackedFiles(string repositoryPath, out List<string> paths, out string error)
        {
            if (!RunGit(repositoryPath, new[] { "ls-files", "--others", "--exclude-standard" }, out var output, out error))
            {
                paths = null;
                return false;
            }

            paths = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) paths.Add(trimmed);
            }

            return true;
        }

        /// <summary>Lists local branch names (short form).</summary>
        public static bool TryListBranches(string repositoryPath, out List<string> branches, out string error)
        {
            if (!RunGit(repositoryPath, new[] { "for-each-ref", "--format=%(refname:short)", "refs/heads/" }, out var output, out error))
            {
                branches = null;
                return false;
            }

            branches = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    branches.Add(trimmed);
                }
            }

            return true;
        }

        /// <summary>Lists the <paramref name="count"/> most recent commits reachable from
        /// <paramref name="revision"/> (defaults to HEAD when null/empty).</summary>
        public static bool TryListRecentCommits(string repositoryPath, int count, out List<CommitInfo> commits, out string error, string revision = null)
        {
            var args = new List<string> { "log", $"-{count}", "--pretty=format:%h\t%s" };
            if (!string.IsNullOrEmpty(revision))
            {
                args.Add(revision);
            }

            if (!RunGit(repositoryPath, args, out var output, out error))
            {
                commits = null;
                return false;
            }

            commits = new List<CommitInfo>();
            foreach (var line in output.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                var parts = line.Split('\t', 2);
                if (parts.Length < 2) continue;
                commits.Add(new CommitInfo(parts[0], parts[1]));
            }

            return true;
        }

        /// <summary>Resolves a revision expression (branch name, hash, "HEAD~1", ...) to its
        /// short hash and subject line, for display purposes (e.g. column headers).</summary>
        public static bool TryGetCommitInfo(string repositoryPath, string revision, out CommitInfo commit, out string error)
        {
            if (!RunGit(repositoryPath, new[] { "log", "-1", "--pretty=format:%h\t%s", revision }, out var output, out error))
            {
                commit = default;
                return false;
            }

            var parts = output.Trim().Split('\t', 2);
            if (parts.Length < 2)
            {
                commit = default;
                error = "unexpected git log output";
                return false;
            }

            commit = new CommitInfo(parts[0], parts[1]);
            return true;
        }

        public static bool TryGetFileAtRevision(
            string repositoryPath,
            string revision,
            string relativeFilePath,
            out string content,
            out string error)
        {
            if (IsWorkingTree(revision))
            {
                var fullPath = Path.Combine(repositoryPath, relativeFilePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                {
                    content = null;
                    error = "file does not exist on disk (deleted in the working tree)";
                    return false;
                }

                content = File.ReadAllText(fullPath, Encoding.UTF8);
                error = null;
                return true;
            }

            var arg = $"{revision}:{relativeFilePath.Replace('\\', '/')}";
            if (!RunGit(repositoryPath, new[] { "show", arg }, out content, out error))
            {
                content = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Walks up from <paramref name="startPath"/> looking for a ".git" directory.
        /// </summary>
        public static string FindRepositoryRoot(string startPath)
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }
}
