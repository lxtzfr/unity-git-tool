using System.Collections.Generic;

namespace UnityGitTool
{
    /// <summary>One row of the property table: a single field compared between revision A and B,
    /// plus the merge result column. A row only exists for fields that actually differ — an
    /// unchanged field has nothing to show here, same as a real `git diff`.
    /// Values are typed (<see cref="UnityEngine.Vector3"/>, <see cref="float"/>, <see cref="string"/>, ...) so
    /// <see cref="FieldFactory"/> can render the same control Unity's Inspector would use for that
    /// type, instead of a plain string. A null value means the field doesn't exist on that side
    /// (added/removed object). <see cref="IsConflict"/> means both sides changed the field to
    /// different values, so <see cref="Result"/> is left null for the user to pick; otherwise only
    /// one side changed it and <see cref="Result"/> is pre-filled with that side's value
    /// (auto-mergeable). Still hand-built everywhere — the scene/prefab YAML diff parser that would
    /// populate these for a real GameObject/component doesn't exist yet.</summary>
    internal class MockRow
    {
        public string Property;
        public object ValueA;
        public object ValueB;
        public object Result;
        public bool IsConflict;

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
    }

    /// <summary>
    /// Shared row storage for the left tree ↔ right table link: <see cref="UnityGitToolWindow"/>'s
    /// tree (see the <c>.Tree</c> partial) registers each node's rows here by node id when it builds
    /// the tree via <see cref="UnityYamlDiffBuilder"/>, and looks them up again on selection to
    /// populate the table. A GameObject node's rows include its own changed fields plus every one of
    /// its components' — the tree only goes one level deep (file &gt; GameObject), components aren't
    /// separate tree nodes.
    /// </summary>
    internal static class MockDataSource
    {
        public static readonly Dictionary<int, List<MockRow>> RowsByNodeId = new();
    }
}
