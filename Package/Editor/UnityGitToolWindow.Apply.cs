using System.IO;
using System.Linq;
using UnityEditor;

namespace UnityGitTool
{
    /// <summary>Write-back: takes whatever's currently resolved in <see cref="MockDataSource.RowsByNodeId"/>
    /// for the selected file and turns it into an actual file on disk plus a `git add` — the piece
    /// that used to be missing entirely (Take A/B/Revert only ever mutated an in-memory row).</summary>
    public partial class UnityGitToolWindow
    {
        private void ApplyResolution()
        {
            if (_selectedNode?.FilePath == null)
            {
                SetStatus("Select a file (not a GameObject) in the tree to apply its resolution.");
                return;
            }
            if (_revisionA != WorkingTree)
            {
                SetStatus("Apply resolution needs A to be Working Tree — use Auto-config merge branches, or pick it manually.");
                return;
            }
            if (!MockDataSource.RowsByNodeId.TryGetValue(_selectedNode.Id, out var rows) || rows.Count == 0)
            {
                SetStatus($"{_selectedNode.Label}: nothing to apply.");
                return;
            }

            var path = _selectedNode.FilePath;
            var fullPath = Path.Combine(GitFileReader.RepositoryRoot, path);
            if (!File.Exists(fullPath))
            {
                SetStatus($"{path}: not found on disk.");
                return;
            }

            var baseText = File.ReadAllText(fullPath);
            var otherText = GitFileReader.ReadFileAtRevision(_revisionB, path);
            if (otherText == null)
            {
                SetStatus($"{path}: couldn't read {_revisionB} — nothing applied.");
                return;
            }

            var baseDocs = UnityYamlParser.Parse(baseText);
            var otherDocs = UnityYamlParser.Parse(otherText);
            var (patched, skipped) = UnityYamlWriter.Apply(baseText, baseDocs, otherText, otherDocs, rows);

            File.WriteAllText(fullPath, patched);
            AssetDatabase.ImportAsset(GetProjectRelativePath(fullPath));

            if (skipped.Count == 0)
            {
                GitFileReader.StageFile(path);
                SetStatus($"{path}: resolution applied and staged (git add). Continue the {_revisionB} yourself (commit / rebase --continue).");
            }
            else
            {
                // Deliberately NOT staged: `git add` marks a path fully resolved, and this file still
                // has fields this couldn't safely write (unresolved conflicts, or manually-typed
                // values — see UnityYamlWriter). The rest of the file's resolution is written and on
                // disk regardless — only staging waits until every field is.
                var preview = string.Join("; ", skipped.Take(4));
                var more = skipped.Count > 4 ? $" (+{skipped.Count - 4} more)" : "";
                SetStatus($"{path}: applied, but not staged — {skipped.Count} field(s) still need attention: {preview}{more}");
            }

            // Selection resets on rebuild (tree node ids are regenerated), so re-select nothing rather
            // than a now-stale node — same as any other revision/refresh change.
            RefreshTree();
        }

        /// <summary>Path relative to the Unity project's Assets folder's parent — what
        /// <see cref="UnityEditor.AssetDatabase.ImportAsset"/> needs, as opposed to the repo-relative
        /// path <see cref="GitFileReader"/> works with (the two differ whenever the repo root isn't
        /// the Unity project root).</summary>
        private static string GetProjectRelativePath(string fullPath)
        {
            var projectRoot = Directory.GetParent(UnityEngine.Application.dataPath)?.FullName ?? UnityEngine.Application.dataPath;
            return Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
        }
    }
}
