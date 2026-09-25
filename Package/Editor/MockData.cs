using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGitTool
{
    /// <summary>One row of the property table: a single field compared between revision A and B,
    /// plus the merge result column. A row only exists for fields that actually differ — an
    /// unchanged field has nothing to show here, same as a real `git diff`.
    /// Values are typed (<see cref="Vector3"/>, <see cref="float"/>, <see cref="string"/>, ...) so
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
    }

    internal enum MockNodeKind
    {
        SceneFile,
        PrefabFile,
        GameObject,
        ComponentTransform,
        ComponentMeshRenderer,
        ComponentGeneric,
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
    /// Hardcoded placeholder data standing in for the real git/parse/diff pipeline — only here to
    /// shape the window's layout before that pipeline exists. The scenario is a merge of two
    /// branches touching the Marshalling scene and the A320 prefab:
    /// - <c>PushbackTug</c>: both sides rotated it differently (conflict) but only Theirs moved it
    ///   on the X axis (auto-mergeable) and repainted its material (auto-mergeable).
    /// - <c>WheelChock_02</c>: added by Theirs only — Ours has no "before" value for its fields.
    /// - <c>OldBarrier</c>: removed by Theirs, untouched by Ours — auto-mergeable deletion.
    /// - <c>PFB_A320.prefab/A320</c>: health tuning is auto-mergeable, but both sides changed the
    ///   damage-zone radius to different values (conflict).
    /// GameObjects with no field changes of their own (only their components differ) have an
    /// empty row list, same as a real Inspector would show nothing to diff at that level.
    /// </summary>
    internal static class MockDataSource
    {
        public static readonly Dictionary<int, List<MockRow>> RowsByNodeId = new();

        public static List<UnityEngine.UIElements.TreeViewItemData<MockNode>> BuildTree()
        {
            var nextId = 0;
            int NewId() => nextId++;

            int Leaf(string label, MockNodeKind kind, List<MockRow> rows, out MockNode node)
            {
                var id = NewId();
                RowsByNodeId[id] = rows;
                node = new MockNode { Id = id, Label = label, Kind = kind, HasConflict = rows.Exists(r => r.IsConflict) };
                return id;
            }

            UnityEngine.UIElements.TreeViewItemData<MockNode> LeafItem(string label, MockNodeKind kind, List<MockRow> rows)
            {
                var id = Leaf(label, kind, rows, out var node);
                return new UnityEngine.UIElements.TreeViewItemData<MockNode>(id, node);
            }

            // ---- PushbackTug: modified, one real conflict on rotation ----

            var transformRows = new List<MockRow>
            {
                new()
                {
                    Property = "Position",
                    ValueA = new Vector3(12.4f, 1f, 0f),
                    ValueB = new Vector3(15f, 1f, 0f),
                    Result = new Vector3(15f, 1f, 0f),
                },
                new()
                {
                    Property = "Rotation",
                    ValueA = Vector3.zero,
                    ValueB = new Vector3(0f, 45f, 0f),
                    IsConflict = true,
                },
            };
            var meshRendererRows = new List<MockRow>
            {
                new() { Property = "Material", ValueA = "M_Tug_Body", ValueB = "M_Tug_Body_Repainted", Result = "M_Tug_Body_Repainted" },
            };
            var pushbackTugChildren = new List<UnityEngine.UIElements.TreeViewItemData<MockNode>>
            {
                LeafItem("Transform", MockNodeKind.ComponentTransform, transformRows),
                LeafItem("MeshRenderer", MockNodeKind.ComponentMeshRenderer, meshRendererRows),
            };
            var pushbackTugId = Leaf("PushbackTug", MockNodeKind.GameObject, new List<MockRow>(), out var pushbackTugNode);
            pushbackTugNode.Badge = MockBadge.Modified;
            pushbackTugNode.HasConflict = true; // aggregated from Transform below

            // ---- WheelChock_02: added by Theirs, no "before" values ----

            var wheelChockItem = LeafItem("WheelChock_02", MockNodeKind.GameObject, new List<MockRow>
            {
                new() { Property = "Name", ValueA = null, ValueB = "WheelChock_02", Result = "WheelChock_02" },
                new() { Property = "Position", ValueA = null, ValueB = new Vector3(3.2f, 0f, -1.1f), Result = new Vector3(3.2f, 0f, -1.1f) },
            });
            wheelChockItem.data.Badge = MockBadge.Added;

            // ---- OldBarrier: removed by Theirs, untouched by Ours ----

            var oldBarrierItem = LeafItem("OldBarrier", MockNodeKind.GameObject, new List<MockRow>
            {
                new() { Property = "Name", ValueA = "OldBarrier", ValueB = null, Result = null },
            });
            oldBarrierItem.data.Badge = MockBadge.Removed;

            // ---- FieldShowcase: one component exercising every FieldFactory-supported type ----

            var defaultMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
            var spriteMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Sprites-Default.mat");
            var whiteTexture = Texture2D.whiteTexture;
            var showcaseConfigA = ScriptableObject.CreateInstance<ScriptableObject>();
            var showcaseConfigB = ScriptableObject.CreateInstance<ScriptableObject>();

            var fadeInCurveA = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            var fadeInCurveB = AnimationCurve.Linear(0f, 0f, 1f, 1f);

            var rampA = new Gradient();
            rampA.SetKeys(
                new[] { new GradientColorKey(Color.red, 0f), new GradientColorKey(Color.yellow, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            var rampB = new Gradient();
            rampB.SetKeys(
                new[] { new GradientColorKey(Color.blue, 0f), new GradientColorKey(Color.cyan, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.5f, 1f) });

            var showcaseRows = new List<MockRow>
            {
                new() { Property = "Tags", ValueA = new[] { "Ramp", "Marshalling" }, ValueB = new[] { "Ramp", "Marshalling", "Winter" }, Result = new[] { "Ramp", "Marshalling", "Winter" } },
                new() { Property = "Waypoints", ValueA = new[] { Vector3.zero, new Vector3(2, 0, 0) }, ValueB = new[] { Vector3.zero, new Vector3(2, 0, 1) }, IsConflict = true },
                new() { Property = "Reference Material", ValueA = defaultMaterial, ValueB = spriteMaterial, IsConflict = true },
                new() { Property = "Icon", ValueA = null, ValueB = whiteTexture, Result = whiteTexture },
                new() { Property = "Config", ValueA = showcaseConfigA, ValueB = showcaseConfigB, IsConflict = true },
                new() { Property = "Fade Curve", ValueA = fadeInCurveA, ValueB = fadeInCurveB, IsConflict = true },
                new() { Property = "Color Ramp", ValueA = rampA, ValueB = rampB, IsConflict = true },
                new() { Property = "Cast Shadows", ValueA = ShadowCastingMode.On, ValueB = ShadowCastingMode.Off, Result = ShadowCastingMode.Off },
                new() { Property = "Collision Mask", ValueA = (LayerMask)(1 << 0 | 1 << 8), ValueB = (LayerMask)(1 << 0), Result = (LayerMask)(1 << 0) },
                new() { Property = "Spawn Area", ValueA = new Rect(0, 0, 100, 50), ValueB = new Rect(10, 10, 120, 60), IsConflict = true },
                new() { Property = "Trigger Bounds", ValueA = new Bounds(Vector3.zero, Vector3.one), ValueB = new Bounds(Vector3.one, Vector3.one * 2f), Result = new Bounds(Vector3.one, Vector3.one * 2f) },
            };
            var fieldShowcaseItem = LeafItem("FieldShowcase", MockNodeKind.GameObject, new List<MockRow>());
            fieldShowcaseItem.data.Badge = MockBadge.Modified;
            var showcaseComponentItem = LeafItem("ShowcaseComponent", MockNodeKind.ComponentGeneric, showcaseRows);
            fieldShowcaseItem = new UnityEngine.UIElements.TreeViewItemData<MockNode>(fieldShowcaseItem.id, fieldShowcaseItem.data, new List<UnityEngine.UIElements.TreeViewItemData<MockNode>> { showcaseComponentItem });
            fieldShowcaseItem.data.HasConflict = true; // aggregated from ShowcaseComponent above

            var sceneChildren = new List<UnityEngine.UIElements.TreeViewItemData<MockNode>>
            {
                new(pushbackTugId, pushbackTugNode, pushbackTugChildren),
                wheelChockItem,
                oldBarrierItem,
                fieldShowcaseItem,
            };
            var sceneFileId = Leaf("Marshalling.unity", MockNodeKind.SceneFile, new List<MockRow>(), out var sceneFileNode);
            sceneFileNode.Badge = MockBadge.Modified;
            sceneFileNode.HasConflict = true;

            // ---- PFB_A320.prefab: health tuning auto-merges, damage radius conflicts ----

            var healthRows = new List<MockRow>
            {
                new() { Property = "Max Health", ValueA = 100f, ValueB = 150f, Result = 150f },
            };
            var damageZoneRows = new List<MockRow>
            {
                new() { Property = "Radius", ValueA = 1.5f, ValueB = 2.2f, IsConflict = true },
            };
            var a320Children = new List<UnityEngine.UIElements.TreeViewItemData<MockNode>>
            {
                LeafItem("HealthComponent", MockNodeKind.ComponentGeneric, healthRows),
                LeafItem("DamageZone", MockNodeKind.ComponentGeneric, damageZoneRows),
            };
            var a320Id = Leaf("A320", MockNodeKind.GameObject, new List<MockRow>(), out var a320Node);
            a320Node.Badge = MockBadge.Modified;
            a320Node.HasConflict = true; // aggregated from DamageZone below

            var prefabFileId = Leaf("PFB_A320.prefab", MockNodeKind.PrefabFile, new List<MockRow>(), out var prefabFileNode);
            prefabFileNode.Badge = MockBadge.Modified;
            prefabFileNode.HasConflict = true;

            return new List<UnityEngine.UIElements.TreeViewItemData<MockNode>>
            {
                new(sceneFileId, sceneFileNode, sceneChildren),
                new(prefabFileId, prefabFileNode, new List<UnityEngine.UIElements.TreeViewItemData<MockNode>>
                {
                    new(a320Id, a320Node, a320Children),
                }),
            };
        }
    }
}
