using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace VisualGitDiff
{
    /// <summary>
    /// A vertical list of clickable rows with a single highlighted selection, backing both the
    /// file list and the object list in <see cref="VisualGitDiffWindow"/>. Row content is
    /// supplied by the caller; this class only owns row creation, click wiring, and the
    /// selected-row highlight.
    /// </summary>
    internal sealed class SelectableList<T>
    {
        private static readonly Color HoverBg = new(0.5f, 0.5f, 0.5f, 0.07f);
        private static readonly Color SelectedBg = new(0.5f, 0.5f, 0.5f, 0.12f);

        private readonly ScrollView _container;
        private readonly List<VisualElement> _rows = new();
        private readonly List<T> _items = new();
        private Action<T> _onSelect;
        private int _selectedIndex = -1;

        public ScrollView Container => _container;

        public SelectableList(ScrollView container)
        {
            _container = container;
        }

        public void SetItems(
            IEnumerable<T> items, Func<T, VisualElement> buildRowContent, Action<T> onSelect,
            FlexDirection direction = FlexDirection.Column, Align alignItems = Align.FlexStart,
            Func<T, string> groupKeySelector = null)
        {
            _container.Clear();
            _rows.Clear();
            _items.Clear();
            _onSelect = onSelect;
            _selectedIndex = -1;

            string previousGroup = null;
            var isFirst = true;

            foreach (var item in items)
            {
                if (groupKeySelector != null)
                {
                    var group = groupKeySelector(item);
                    if (isFirst || group != previousGroup)
                    {
                        _container.Add(BuildGroupHeader(group));
                        previousGroup = group;
                    }
                }
                isFirst = false;

                var index = _items.Count;
                var row = new Button(() => Select(item))
                {
                    style =
                    {
                        unityTextAlign = TextAnchor.UpperLeft, flexDirection = direction, alignItems = alignItems,
                        borderLeftWidth = 3, borderLeftColor = Color.clear,
                        borderTopLeftRadius = 0, borderTopRightRadius = 0, borderBottomLeftRadius = 0, borderBottomRightRadius = 0,
                        marginLeft = 0, marginRight = 0, marginTop = 0,
                        paddingLeft = 8, paddingTop = 6, paddingBottom = 6,
                        borderBottomWidth = 1, borderBottomColor = new Color(0, 0, 0, 0.15f),
                    },
                };
                row.Add(buildRowContent(item));
                row.RegisterCallback<MouseEnterEvent>(_ =>
                {
                    if (index != _selectedIndex) row.style.backgroundColor = HoverBg;
                });
                row.RegisterCallback<MouseLeaveEvent>(_ =>
                {
                    if (index != _selectedIndex) row.style.backgroundColor = Color.clear;
                });

                _items.Add(item);
                _rows.Add(row);
                _container.Add(row);
            }
        }

        public void Clear()
        {
            _container.Clear();
            _rows.Clear();
            _items.Clear();
            _onSelect = null;
            _selectedIndex = -1;
        }

        public void Select(T item)
        {
            _selectedIndex = _items.FindIndex(i => EqualityComparer<T>.Default.Equals(i, item));

            for (var i = 0; i < _rows.Count; i++)
            {
                Highlight(_rows[i], i == _selectedIndex);
            }

            _onSelect?.Invoke(item);
        }

        private static void Highlight(VisualElement row, bool isSelected)
        {
            row.style.borderLeftColor = isSelected ? DiffTheme.ModifiedColor : Color.clear;
            row.style.backgroundColor = isSelected ? SelectedBg : Color.clear;
        }

        private static VisualElement BuildGroupHeader(string text) => new Label(text)
        {
            style =
            {
                fontSize = 9, unityFontStyleAndWeight = FontStyle.Bold, opacity = 0.5f,
                paddingLeft = 8, paddingTop = 8, paddingBottom = 3,
            },
        };
    }
}
