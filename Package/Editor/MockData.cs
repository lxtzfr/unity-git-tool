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

    /// <summary>Per-side status shown in a header row's A/B columns (see <see cref="MockRow.HeaderStateA"/>/
    /// <see cref="MockRow.HeaderStateB"/> and <see cref="MockNode.HeaderStateA"/>/<see cref="MockNode.HeaderStateB"/>) —
    /// distinct from <see cref="MockRow.MarkedForDeletion"/>/<see cref="MockNode.MarkedForDeletion"/>,
    /// which is the user's own delete action rather than this pre-existing diff status. Computed by
    /// <c>UnityYamlDiffBuilder.ResolveHeaderStates</c>.</summary>
    internal enum MockHeaderState
    {
        /// <summary>Unchanged — never actually shown, since a header row only appears when something
        /// about it changed.</summary>
        None,
        /// <summary>This side doesn't have the object/component at all.</summary>
        Missing,
        Added,
        Deleted,
        /// <summary>Both sides have it, but at least one field differs.</summary>
        Modified,
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
        /// <see cref="HeaderIcon"/> the Unity icon content name to show next to it. <see cref="FileId"/>
        /// is set here too (unlike a plain field row, where it's the FIELD's owner) — this component's
        /// own id, so a "Delete Component" action on this row knows what to remove.</summary>
        public bool IsHeader;
        public string HeaderIcon;

        /// <summary>This header's per-side diff status (see <see cref="MockHeaderState"/>), shown in
        /// the A/B columns instead of a field value — set alongside the rest of a component header row
        /// (see <c>UnityYamlDiffBuilder.BuildComponentRows</c>), or copied from <see cref="MockNode.HeaderStateA"/>/
        /// <see cref="MockNode.HeaderStateB"/> onto the synthetic GameObject header row (see
        /// UnityGitToolWindow.Tree.cs). <see cref="MockHeaderState.None"/> (the default) on every
        /// non-header row.</summary>
        public MockHeaderState HeaderStateA;
        public MockHeaderState HeaderStateB;

        /// <summary>Set by a "Delete Component" action on this header row (see
        /// UnityGitToolWindow.Table.cs) — this component's whole document is removed on Apply, along
        /// with its entry in the owning GameObject's <c>m_Component</c> list (see
        /// <see cref="UnityYamlWriter"/>). Only ever meaningful on a header row.</summary>
        public bool MarkedForDeletion;

        /// <summary>Set only on the synthetic header row <see cref="UnityGitToolWindow"/>'s table
        /// prepends for whichever GameObject is currently selected (see
        /// UnityGitToolWindow.Tree.cs's OnTreeSelectionChanged) — a "Delete GameObject" click there
        /// toggles this node's own <see cref="MockNode.MarkedForDeletion"/> directly, rather than the
        /// row needing its own separate copy of that state. Null on every other row, including a
        /// component's header — those use <see cref="MarkedForDeletion"/> above instead.</summary>
        public MockNode GameObjectNode;

        /// <summary>Unified read of whichever of <see cref="MarkedForDeletion"/> (a component header)
        /// or <see cref="GameObjectNode"/>'s own flag (the synthetic GameObject header) applies to
        /// this header row — so a caller that just wants "is this thing going away" doesn't need to
        /// know which kind of header it's looking at. Recurses through <see cref="OwnerHeaderRow"/> so
        /// a component header also reads as deleted once its owning GameObject header is (see that
        /// field's doc comment — a component header now carries an OwnerHeaderRow too, pointing at the
        /// GameObject header), and a plain field row under it inherits the same two levels for free.</summary>
        public bool IsMarkedForDeletion => (GameObjectNode?.MarkedForDeletion ?? MarkedForDeletion) || (OwnerHeaderRow?.IsMarkedForDeletion ?? false);

        /// <summary>The header row this row falls under, set once when <see cref="UnityGitToolWindow"/>'s
        /// table assembles the row list for a selection (see OnTreeSelectionChanged):
        /// <list type="bullet">
        /// <item>on a plain field row — the GameObject's own synthetic header for one of the
        /// GameObject's own fields, or a component's header for one of that component's;</item>
        /// <item>on a component header row itself — the GameObject's synthetic header, so deleting the
        /// whole GameObject is reflected on every component header underneath it too (see
        /// <see cref="IsMarkedForDeletion"/>), consistent with <see cref="UnityYamlWriter"/>'s cascade,
        /// which already removes a deleted GameObject's components regardless of this UI state.</item>
        /// </list>
        /// Lets the Result column (see UnityGitToolWindow.Table.cs) show a field as empty rather than a
        /// stale resolved value once its whole owner is marked for deletion. Null on the GameObject's
        /// own synthetic header (nothing owns it), and on the rare row this tool couldn't associate
        /// with a header (defensive only).</summary>
        public MockRow OwnerHeaderRow;
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

        /// <summary>Repo-relative path of the file this node belongs to — set on every node, file or
        /// GameObject, so a GameObject-level action (delete — see <see cref="MarkedForDeletion"/>)
        /// knows which file to patch without needing to walk back up to an ancestor. Also doubles as
        /// the "is this a file node" check on a FILE node specifically for
        /// <see cref="UnityGitToolWindow.ApplyResolution"/> — <see cref="Kind"/> is the real
        /// discriminator once a GameObject node carries one too.</summary>
        public string FilePath;

        /// <summary>0 (Unity's own "no reference" sentinel) on a file node; the real
        /// <see cref="GitYamlDocument.FileId"/> on a GameObject node — what
        /// <see cref="UnityYamlWriter"/> needs to find and remove this object's whole document (and
        /// its own components, and every descendant) when <see cref="MarkedForDeletion"/>.</summary>
        public long FileId;

        /// <summary>Set by a "Delete GameObject" action (see UnityGitToolWindow.Tree.cs's Result tree)
        /// — this whole object, its own components, and every descendant are removed on Apply, along
        /// with the entry pointing at it from its parent's <c>m_Children</c> (see
        /// <see cref="UnityYamlWriter"/>). Never true on a file node.</summary>
        public bool MarkedForDeletion;

        /// <summary>This GameObject's per-side diff status — see <see cref="MockRow.HeaderStateA"/>/
        /// <see cref="MockRow.HeaderStateB"/> for how it's used; set alongside <see cref="Badge"/> in
        /// <c>UnityYamlDiffBuilder.BuildGameObjectSubtree</c>, both <see cref="MockHeaderState.None"/>
        /// when <see cref="Badge"/> is <see cref="MockBadge.None"/> (a structural passthrough node).</summary>
        public MockHeaderState HeaderStateA;
        public MockHeaderState HeaderStateB;
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

        /// <summary>Every GameObject <see cref="MockNode"/> under a given node, itself included,
        /// aggregated the same way <see cref="RowsByNodeId"/> is (see <see cref="UnityYamlDiffBuilder.BuildGameObjectSubtree"/>).
        /// The node objects themselves, not a copy — <see cref="MockNode.MarkedForDeletion"/> is set
        /// later by a UI click, after the tree is built, so <see cref="UnityGitToolWindow.ApplyResolution"/>
        /// re-reads this list (via a file node) at Apply time to see which ones ended up marked,
        /// rather than this dictionary trying to track deletions itself.</summary>
        public static readonly Dictionary<int, List<MockNode>> GameObjectNodesByNodeId = new();
    }
}
