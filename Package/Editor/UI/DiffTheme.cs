using UnityEngine;
using UnityEngine.UIElements;

namespace VisualGitDiff
{
    /// <summary>Shared colors and small status-indicator widgets used across the diff window's panes.</summary>
    internal static class DiffTheme
    {
        public static readonly Color AddedColor = new(0.36f, 0.68f, 0.42f);
        public static readonly Color AddedBg = new(0.36f, 0.68f, 0.42f, 0.16f);
        public static readonly Color RemovedColor = new(0.76f, 0.28f, 0.28f);
        public static readonly Color RemovedBg = new(0.76f, 0.28f, 0.28f, 0.16f);
        public static readonly Color ModifiedColor = new(0.72f, 0.55f, 0.14f);
        public static readonly Color ModifiedBg = new(0.72f, 0.55f, 0.14f, 0.18f);

        public static VisualElement BuildBadge(string badge)
        {
            var (fg, bg) = badge switch
            {
                "A" => (AddedColor, AddedBg),
                "R" or "D" => (RemovedColor, RemovedBg),
                _ => (ModifiedColor, ModifiedBg),
            };
            return new Label(badge)
            {
                style =
                {
                    fontSize = 9, unityFontStyleAndWeight = FontStyle.Bold, color = fg, backgroundColor = bg,
                    paddingLeft = 4, paddingRight = 4, paddingTop = 1, paddingBottom = 1,
                    borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                    width = 14, unityTextAlign = TextAnchor.MiddleCenter,
                },
            };
        }
    }
}
