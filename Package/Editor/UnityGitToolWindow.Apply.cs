using System.Collections.Generic;
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

            var deletedComponentIds = rows.Where(r => r.IsHeader && r.MarkedForDeletion).Select(r => r.FileId).ToList();
            var deletedGameObjectIds = MockDataSource.GameObjectNodesByNodeId.TryGetValue(_selectedNode.Id, out var goNodes)
                ? goNodes.Where(n => n.MarkedForDeletion).Select(n => n.FileId).ToList()
                : new List<long>();

            var (patched, skipped) = UnityYamlWriter.Apply(baseText, baseDocs, otherText, otherDocs, rows, deletedGameObjectIds, deletedComponentIds);

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

        /// <summary>Discards one field's uncommitted change — writes A's own value for it straight
        /// into the Working Tree file on disk, immediately (no Result/Apply step, unlike
        /// <see cref="ApplyResolution"/>) — the plain-diff-mode counterpart to Merge mode's write-back,
        /// wired from a Rollback button that only ever shows on the B column when B is Working Tree
        /// (see UnityGitToolWindow.Table.cs's <c>BuildColumn</c>). Reuses the exact same line-splice
        /// machinery as a real merge Apply (<see cref="UnityYamlWriter"/>) — just fed the current
        /// on-disk content as the text actually being patched (<paramref name="row"/>'s
        /// <see cref="MockRow.FileId"/>/<see cref="MockRow.Key"/> still resolve correctly there, since
        /// Unity's fileIDs are stable across revisions of the same file) and A's revision as the
        /// "other" side to pull the one field's value from (<see cref="MockResolution.B"/> in
        /// <see cref="UnityYamlWriter"/>'s own vocabulary always means "splice in the other side",
        /// regardless of which of this tool's A/B labels that happens to be).</summary>
        private void RollbackField(MockRow row)
        {
            if (_selectedNode?.FilePath == null || row.FileId == 0 || row.Key == null) return;

            var path = _selectedNode.FilePath;
            var fullPath = Path.Combine(GitFileReader.RepositoryRoot, path);
            if (!File.Exists(fullPath))
            {
                SetStatus($"{path}: not found on disk.");
                return;
            }

            var baseText = File.ReadAllText(fullPath); // Working Tree — what actually gets patched
            var otherText = GitFileReader.ReadFileAtRevision(_revisionA, path); // rolling back TO this
            if (otherText == null)
            {
                SetStatus($"{path}: couldn't read {_revisionA} — nothing rolled back.");
                return;
            }

            var baseDocs = UnityYamlParser.Parse(baseText);
            var otherDocs = UnityYamlParser.Parse(otherText);
            var rollbackRow = new MockRow { FileId = row.FileId, Key = row.Key, Property = row.Property, Resolution = MockResolution.B };

            var (patched, skipped) = UnityYamlWriter.Apply(baseText, baseDocs, otherText, otherDocs,
                new List<MockRow> { rollbackRow }, System.Array.Empty<long>(), System.Array.Empty<long>());

            if (skipped.Count > 0)
            {
                SetStatus($"{path}: {row.Property} — couldn't roll back ({skipped[0]}).");
                return;
            }

            File.WriteAllText(fullPath, patched);
            AssetDatabase.ImportAsset(GetProjectRelativePath(fullPath));
            SetStatus($"{path}: {row.Property} rolled back to {_revisionA}.");
            RefreshTree();
        }

        /// <summary>Discards an entire component's or GameObject's uncommitted changes in one write —
        /// the header-row counterpart to <see cref="RollbackField"/>, wired from the bulk Rollback
        /// button on a header's B column (see UnityGitToolWindow.Table.cs's <c>BindHeaderValueCell</c>).
        /// Two different operations depending on whether B (Working Tree) still has the object at all
        /// (<see cref="MockRow.HeaderStateB"/>/<see cref="MockNode.HeaderStateB"/>):
        /// <list type="bullet">
        /// <item>Modified — exists on both sides, some fields differ: rolls back every field row under
        /// this header (<see cref="IsUnderHeader"/>) in one batch, same idea as N calls to
        /// <see cref="RollbackField"/> but one file write instead of N.</item>
        /// <item>Deleted — missing from the working tree entirely: restores the whole document via
        /// <see cref="UnityYamlWriter"/>'s restore path, which doesn't exist for a field-level
        /// splice. A component brings its owning GameObject back too if that's ALSO missing (read off
        /// <paramref name="header"/>'s own <see cref="MockRow.OwnerHeaderRow"/>) rather than leaving a
        /// dangling reference for the writer to just skip. A GameObject brings back every one of its
        /// own components (collected from this same header's descendants in <see cref="_currentRows"/>,
        /// whose <see cref="MockRow.FileId"/> already reflects each component's real id on A's side —
        /// see <c>UnityYamlDiffBuilder.BuildComponentRows</c>) — deliberately not further into any
        /// deleted CHILD GameObjects, which need their own separate restore.</item>
        /// </list></summary>
        private void RollbackHeader(MockRow header)
        {
            if (_selectedNode?.FilePath == null) return;

            var path = _selectedNode.FilePath;
            var fullPath = Path.Combine(GitFileReader.RepositoryRoot, path);
            if (!File.Exists(fullPath))
            {
                SetStatus($"{path}: not found on disk.");
                return;
            }

            var baseText = File.ReadAllText(fullPath);
            var otherText = GitFileReader.ReadFileAtRevision(_revisionA, path);
            if (otherText == null)
            {
                SetStatus($"{path}: couldn't read {_revisionA} — nothing rolled back.");
                return;
            }

            var baseDocs = UnityYamlParser.Parse(baseText);
            var otherDocs = UnityYamlParser.Parse(otherText);

            List<MockRow> rowsToWrite;
            var restoreIds = new List<long>();

            if (header.GameObjectNode != null && header.GameObjectNode.HeaderStateB == MockHeaderState.Deleted)
            {
                restoreIds.Add(header.GameObjectNode.FileId);
                restoreIds.AddRange(_currentRows.Where(r => r.IsHeader && r != header && r.OwnerHeaderRow == header).Select(r => r.FileId));
                rowsToWrite = new List<MockRow>();
            }
            else if (header.FileId != 0 && header.HeaderStateB == MockHeaderState.Deleted)
            {
                restoreIds.Add(header.FileId);
                var ownerGoNode = header.OwnerHeaderRow?.GameObjectNode;
                if (ownerGoNode != null && ownerGoNode.HeaderStateB == MockHeaderState.Deleted)
                    restoreIds.Add(ownerGoNode.FileId);
                rowsToWrite = new List<MockRow>();
            }
            else
            {
                rowsToWrite = _currentRows
                    .Where(r => !r.IsHeader && IsUnderHeader(r, header))
                    .Select(r => new MockRow { FileId = r.FileId, Key = r.Key, Property = r.Property, Resolution = MockResolution.B })
                    .ToList();
            }

            var (patched, skipped) = UnityYamlWriter.Apply(baseText, baseDocs, otherText, otherDocs,
                rowsToWrite, System.Array.Empty<long>(), System.Array.Empty<long>(), restoreIds);

            File.WriteAllText(fullPath, patched);
            AssetDatabase.ImportAsset(GetProjectRelativePath(fullPath));

            if (skipped.Count > 0)
            {
                var preview = string.Join("; ", skipped.Take(4));
                var more = skipped.Count > 4 ? $" (+{skipped.Count - 4} more)" : "";
                SetStatus($"{path}: {header.Property} rolled back, but needs attention — {preview}{more}");
            }
            else
            {
                SetStatus($"{path}: {header.Property} rolled back to {_revisionA}.");
            }
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
