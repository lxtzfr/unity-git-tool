using System.Collections.Generic;
using System.Linq;

namespace UnityGitTool
{
    /// <summary>
    /// Applies a resolved merge back onto revision A's raw text (always the working tree — see
    /// <see cref="UnityGitToolWindow.ApplyResolution"/>) by splicing in revision B's raw lines for
    /// every field resolved to B, and leaving A's own lines untouched otherwise. Never re-serializes
    /// a display value (<see cref="UnityYamlValueConverter.ToDisplayValue"/>) back into YAML — that
    /// conversion is lossy (a resolved reference becomes a name, an array becomes a fresh instance),
    /// so the only two things this ever writes are "A's existing bytes, unmodified" and "B's existing
    /// bytes, copied verbatim" for a field's whole top-level line span (see
    /// <see cref="GitYamlDocument.FieldSpans"/>). A row this can't handle safely (<see cref="MockResolution.Manual"/>
    /// or <see cref="MockResolution.Unresolved"/>, or one missing span info) is left exactly as-is in A
    /// and reported back instead of guessed at.
    /// </summary>
    internal static class UnityYamlWriter
    {
        public static (string Text, List<string> Skipped) Apply(
            string baseText,
            List<GitYamlDocument> baseDocs,
            string otherText,
            List<GitYamlDocument> otherDocs,
            List<MockRow> rows)
        {
            var baseById = baseDocs.Where(d => d.TypeName != null).ToDictionary(d => d.FileId);
            var otherById = otherDocs.Where(d => d.TypeName != null).ToDictionary(d => d.FileId);
            var baseLines = baseText.Replace("\r\n", "\n").Split('\n').ToList();
            var otherLines = otherText.Replace("\r\n", "\n").Split('\n').ToList();
            var skipped = new List<string>();

            var candidates = rows.Where(r => !r.IsHeader && r.FileId != 0 && r.Key != null).ToList();

            // Applied back-to-front (by the base document's own line position, or its BodyEndLine for
            // a pure insert) so splicing one field never shifts the line numbers already recorded for
            // one earlier in the file that hasn't been applied yet.
            var ordered = candidates
                .Select(row =>
                {
                    baseById.TryGetValue(row.FileId, out var baseDoc);
                    var baseSpan = default(LineSpan);
                    // Short-circuiting `&&` never runs TryGetValue (and its `out`) when baseDoc is
                    // null, so baseSpan is assigned separately above first — otherwise it'd be
                    // "possibly unassigned" in that branch even though it's never read there.
                    var hasBaseSpan = baseDoc != null && baseDoc.FieldSpans.TryGetValue(row.Key, out baseSpan);
                    return (row, baseDoc, hasBaseSpan, baseSpan, anchor: hasBaseSpan ? baseSpan.Start : baseDoc?.BodyEndLine ?? -1);
                })
                .Where(e => e.baseDoc != null)
                .OrderByDescending(e => e.anchor)
                .ToList();

            foreach (var (row, baseDoc, hasBaseSpan, baseSpan, _) in ordered)
            {
                switch (row.Resolution)
                {
                    case MockResolution.A:
                        break; // baseText already reflects A — nothing to splice

                    case MockResolution.Unresolved:
                        skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: unresolved conflict — not applied");
                        break;

                    case MockResolution.Manual:
                        skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: manually edited value can't be written back — take A or B instead");
                        break;

                    case MockResolution.B:
                        ApplyTakeB(baseLines, otherLines, baseDoc, hasBaseSpan, baseSpan, otherById, row, skipped);
                        break;
                }
            }

            return (string.Join("\n", baseLines), skipped);
        }

        private static void ApplyTakeB(
            List<string> baseLines,
            List<string> otherLines,
            GitYamlDocument baseDoc,
            bool hasBaseSpan,
            LineSpan baseSpan,
            Dictionary<long, GitYamlDocument> otherById,
            MockRow row,
            List<string> skipped)
        {
            if (!otherById.TryGetValue(baseDoc.FileId, out var otherDoc))
            {
                skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: object not found on the other side — not applied");
                return;
            }

            var hasOtherSpan = otherDoc.FieldSpans.TryGetValue(row.Key, out var otherSpan);
            if (!hasBaseSpan && !hasOtherSpan)
            {
                skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: no line info on either side — not applied");
                return;
            }

            // Remove A's existing lines for this field (none, at BodyEndLine, when it doesn't exist
            // in A — a pure insert), then splice in B's, verbatim, if it has any (none = a deletion,
            // when B doesn't have the field either).
            var removeStart = hasBaseSpan ? baseSpan.Start : baseDoc.BodyEndLine;
            var removeCount = hasBaseSpan ? baseSpan.End - baseSpan.Start : 0;
            baseLines.RemoveRange(removeStart, removeCount);

            if (hasOtherSpan)
                baseLines.InsertRange(removeStart, otherLines.GetRange(otherSpan.Start, otherSpan.End - otherSpan.Start));
        }
    }
}
