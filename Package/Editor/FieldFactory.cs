using System;
using System.Collections;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>
    /// Picks the same field control Unity's own Inspector uses for a value, based on its runtime
    /// type — <see cref="Vector3Field"/> for a position, <see cref="ObjectField"/> for an asset
    /// reference, <see cref="CurveField"/> for an AnimationCurve, etc. — instead of hand-building a
    /// layout per field type. Falls back to a read-only <see cref="Label"/> for a null value (a
    /// field that doesn't exist on this side, e.g. an added/removed object) or an unrecognized type.
    /// </summary>
    internal static class FieldFactory
    {
        /// <summary>
        /// <paramref name="onValueChanged"/>, when given, wires the control's native change event
        /// (whatever type it is — Vector3Field, ColorField, ...) to report the new value back as a
        /// boxed <see cref="object"/>, so a caller can write it into its own model without needing
        /// to know which concrete field type got created.
        /// </summary>
        public static VisualElement Create(object value, bool enabled = false, Action<object> onValueChanged = null)
        {
            // A null value means the field doesn't exist on this side (added/removed) — just leave
            // the cell empty rather than a placeholder control or "—" text.
            if (value == null) return new VisualElement();

            VisualElement field = value switch
            {
                Vector3 v3 => new Vector3Field { value = v3 },
                Vector2 v2 => new Vector2Field { value = v2 },
                Vector4 v4 => new Vector4Field { value = v4 },
                Quaternion q => new Vector3Field { value = q.eulerAngles, tooltip = "Euler angles" },
                Color c => new ColorField { value = c, showAlpha = true },
                Rect r => new RectField { value = r },
                Bounds bd => new BoundsField { value = bd },
                AnimationCurve curve => new CurveField { value = curve },
                Gradient g => new GradientField { value = g },
                LayerMask lm => new LayerMaskField { value = lm.value },
                Enum e => new EnumField(e),
                float f => new FloatField { value = f },
                int i => new IntegerField { value = i },
                bool b => new Toggle { value = b },
                string s => new TextField { value = s },
                UnityEngine.Object obj => new ObjectField { objectType = obj.GetType(), value = obj },
                IEnumerable list => BuildArrayBlock(list, enabled),
                _ => new Label(value.ToString()),
            };

            if (field is not Label)
            {
                field.SetEnabled(enabled);
                field.AddToClassList("gt-value-field");
                if (onValueChanged != null) WireChangeCallback(field, onValueChanged);
            }
            return field;
        }

        private static void WireChangeCallback(VisualElement field, Action<object> onValueChanged)
        {
            switch (field)
            {
                case Vector3Field f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case Vector2Field f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case Vector4Field f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case ColorField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case RectField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case BoundsField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case CurveField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case GradientField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case LayerMaskField f: f.RegisterValueChangedCallback(e => onValueChanged((LayerMask)e.newValue)); break;
                case EnumField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case FloatField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case IntegerField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case Toggle f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case TextField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
                case ObjectField f: f.RegisterValueChangedCallback(e => onValueChanged(e.newValue)); break;
            }
        }

        /// <summary>Arrays/lists render as a stacked block of "[i] &lt;field&gt;" rows, same idea as
        /// Unity's array Inspector but without the reorder/size chrome — this is a read-only diff
        /// view, not an editor.</summary>
        private static VisualElement BuildArrayBlock(IEnumerable list, bool enabled)
        {
            var container = new VisualElement();
            container.AddToClassList("gt-array-block");

            var index = 0;
            foreach (var item in list)
            {
                var row = new VisualElement();
                row.AddToClassList("gt-array-row");
                var indexLabel = new Label($"[{index}]");
                indexLabel.AddToClassList("gt-array-index");
                row.Add(indexLabel);
                row.Add(Create(item, enabled: enabled));
                container.Add(row);
                index++;
            }
            if (index == 0) container.Add(new Label("(empty)") { style = { opacity = 0.5f } });
            return container;
        }

        /// <summary>Colors the value text itself (green/red/orange for added/removed/modified)
        /// instead of a background or a side border — works for any field type <see cref="Create"/>
        /// returns. Scalar fields (FloatField, TextField, ...) and compound ones (Vector3Field, ...)
        /// show their digits inside a "unity-text-input" wrapper; fields without one (EnumField,
        /// LayerMaskField, ObjectField's display label) show it via a plain TextElement instead.</summary>
        public static void SetValueColor(VisualElement field, Color? color)
        {
            StyleColor styleColor = color.HasValue ? color.Value : StyleKeyword.Null;

            var textInputs = field.Query(name: "unity-text-input").ToList();
            if (textInputs.Count > 0)
            {
                foreach (var textInput in textInputs)
                {
                    var textElement = textInput.Q<TextElement>();
                    if (textElement != null) textElement.style.color = styleColor;
                }
                return;
            }

            foreach (var textElement in field.Query<TextElement>().ToList())
                textElement.style.color = styleColor;
        }

        /// <summary>For a compound value (Vector2/Vector3), tints only the sub-fields (X/Y/Z) that
        /// actually differ from <paramref name="otherValue"/> — e.g. a rotation conflict where only
        /// Y changed shouldn't highlight X and Z too. No-op for scalar types or when either side is
        /// null (added/removed field — nothing to compare per-axis).</summary>
        public static void HighlightChangedComponents(VisualElement field, object value, object otherValue, Color color)
        {
            if (value is Vector3 v3 && otherValue is Vector3 o3)
            {
                TintAxis(field, "unity-x-input", v3.x != o3.x, color);
                TintAxis(field, "unity-y-input", v3.y != o3.y, color);
                TintAxis(field, "unity-z-input", v3.z != o3.z, color);
            }
            else if (value is Vector2 v2 && otherValue is Vector2 o2)
            {
                TintAxis(field, "unity-x-input", v2.x != o2.x, color);
                TintAxis(field, "unity-y-input", v2.y != o2.y, color);
            }
        }

        private static void TintAxis(VisualElement field, string inputName, bool changed, Color color)
        {
            var input = field.Q(inputName);
            if (input == null) return;
            // FloatField's own children are the "X"/"Y"/"Z" prefix Label *and* the actual digits,
            // nested inside "unity-text-input" — Label is itself a TextElement, so a plain
            // Q<TextElement>() from the FloatField would grab the prefix instead of the value.
            var textInput = input.Q("unity-text-input") ?? input;
            var textElement = textInput.Q<TextElement>() ?? textInput;
            textElement.style.color = changed ? color : StyleKeyword.Null;
        }
    }
}
