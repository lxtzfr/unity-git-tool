using System.Collections.Generic;

namespace UnityGitTool
{
    /// <summary>Which side a row's <see cref="MockRow.Result"/> actually came from — tracked
    /// explicitly rather than inferred by comparing <c>Result</c> back against <c>ValueA</c>/<c>ValueB</c>,
    /// because those are independently-computed <em>display</em> values (see
    /// <see cref="UnityYamlValueConverter.ToDisplayValue"/>) that can be equal in content but not in
    /// `.Equals` (arrays, resolved reference objects) — an explicit tag is the only reliable way for
    /// <see cref="UnityYamlWriter"/> to know which raw YAML span to splice back in.</summary>
    internal enum MockResolution
    {
        /// <summary>Both sides agree, or only one side has the field — nothing to pick.</summary>
        Auto,
        A,
        B,
        /// <summary>A conflict with no pick made yet (or reverted back to this).</summary>
        Unresolved,
        /// <summary>The Result field was typed into directly — a real value, but not one
        /// <see cref="UnityYamlWriter"/> can safely re-serialize back into YAML, so it's excluded
        /// from write-back and reported instead of guessed at.</summary>
        Manual,
    }

    /// <summary>One row of the property table: a single field compared between revision A and B,
    /// plus the merge result column. A row only exists for fields that actually differ — an
    /// unchanged field has nothing to show here, same as a real `git diff`.
    /// Values are typed (<see cref="UnityEngine.Vector3"/>, <see cref="float"/>, <see cref="string"/>, ...) so
    /// <see cref="FieldFactory"/> can render the same control Unity's Inspector would use for that
    /// type, instead of a plain string. A null value means the field doesn't exist on that side
    /// (added/removed object). <see cref="IsConflict"/> means both sides changed the field to
    /// different values, so <see cref="Result"/> is left null for the user to pick; otherwise only
    /// one side changed it and <see cref="Result"/> is pre-filled with that side's value
    /// (auto-mergeable).</summary>
    internal class MockRow
    {
        public string Property;
        public object ValueA;
        public object ValueB;
        public object Result;
        public bool IsConflict;
        public MockResolution Resolution;

        /// <summary>The owning YAML document's <see cref="GitYamlDocument.FileId"/> and this field's
        /// raw YAML key (as opposed to <see cref="Property"/>, its humanized display label) — the
        /// identity <see cref="UnityYamlWriter"/> needs to find this field's line span again. 0/null
        /// on a header row, which carries no field of its own.</summary>
        public long FileId;
        public string Key;

        /// <summary>Same meaning as <see cref="UnityYamlDiffBuilder.BuildFileNode"/>'s parameter of the
        /// same name — whether B is actually the OLDER side despite being on the "B" side of every
        /// other A/B convention in this tool (the HEAD-fallback for a non-conflicted file — see
        /// UnityGitToolWindow.Tree.cs). GetStatusColor (in the <c>.Table</c> partial) needs this
        /// per-row, not just per-file like <see cref="UnityYamlDiffBuilder.ResolveBadge"/> gets it,
        /// since it colors A/B cells directly off <see cref="ValueA"/>/<see cref="ValueB"/> being null
        /// — the same Added/Removed-direction question as a tree badge.</summary>
        public bool BIsOlderBaseline;

        /// <summary>A component-header row — Inspector-style icon + name bar separating one merged
        /// component's rows from the next in the flat property list (see <see cref="UnityYamlDiffBuilder"/>).
        /// Carries no value of its own; <see cref="Property"/> is the component's label and
        /// <see cref="HeaderIcon"/> the Unity icon content name to show next to it.</summary>
        public bool IsHeader;
        public string HeaderIcon;
    }

    internal enum MockNodeKind
    {
        SceneFile,
        PrefabFile,
        GameObject,
    }

    internal enum MockBadge
    {
        None,
        Added,
        Modified,
        Removed,
    }

    /// <summary>One node of the left tree (file / GameObject / component). Rows are looked up by
    /// <see cref="Id"/> in <see cref="MockDataSource.RowsByNodeId"/>. <see cref="Kind"/> picks the
    /// type icon, <see cref="Badge"/> the git-status marker, <see cref="HasConflict"/> whether the
    /// warning icon shows (this node or one of its descendants has a row needing a merge decision).</summary>
    internal class MockNode
    {
        public int Id;
        public string Label;
        public MockNodeKind Kind;
        public MockBadge Badge;
        public bool HasConflict;

        /// <summary>Repo-relative path — set only on a file node (<see cref="MockNodeKind.SceneFile"/>/
        /// <see cref="MockNodeKind.PrefabFile"/>); null on a GameObject node. Doubles as the "is this
        /// a file node" check for <see cref="UnityGitToolWindow.ApplyResolution"/>, which only makes
        /// sense selected at file granularity (it writes the whole file in one pass).</summary>
        public string FilePath;
    }

    /// <summary>
    /// Shared row storage for the left tree ↔ right table link — <see cref="UnityYamlDiffBuilder"/>
    /// registers each node's rows here by node id as it builds the tree. Two parallel dictionaries,
    /// same keys, different scope:
    /// <list type="bullet">
    /// <item><see cref="OwnRowsByNodeId"/> — just this node's own changed fields plus its own
    /// components' (never a child GameObject's). What <see cref="UnityGitToolWindow"/>'s tree
    /// selection shows in the table — a child GameObject's components need to read as belonging to
    /// that child, not bleed into its parent's row list just because the parent happens to be
    /// selected.</item>
    /// <item><see cref="RowsByNodeId"/> — the same, plus every nested child's rows recursively. What
    /// <see cref="UnityGitToolWindow.ApplyResolution"/> reads (via a file node, which is always this
    /// aggregated form — see <see cref="UnityYamlDiffBuilder.BuildFileNode"/>) to resolve a whole
    /// file in one pass without needing to visit every GameObject in it individually.</item>
    /// </list>
    /// </summary>
    internal static class MockDataSource
    {
        public static readonly Dictionary<int, List<MockRow>> RowsByNodeId = new();
        public static readonly Dictionary<int, List<MockRow>> OwnRowsByNodeId = new();
    }
}
