using System.Windows.Media;

namespace CodeAstrogator.Editor
{
    /// <summary>
    /// Added/removed colors of the inline edit review (adorner + scrollbar marks): green/red, or blue/orange when
    /// "Color-blind friendly diffs" is on (settings window, <c>AstrogatorOptions.ColorBlindDiffs</c>; set by the
    /// bridge). The WebUI panel switches the same way via the <c>diffs-cb</c> class. Existing adornments pick a
    /// change up on their next redraw.
    /// </summary>
    internal static class EditReviewPalette
    {
        public static bool ColorBlind { get; set; }

        public static Set Current => ColorBlind ? ColorBlindSet : Standard;

        private static readonly Set Standard = new Set(
            add: Color.FromRgb(0x3F, 0xB9, 0x50), del: Color.FromRgb(0xF8, 0x51, 0x49),
            addSolid: Color.FromArgb(0xF0, 0x2E, 0x7D, 0x39), delSolid: Color.FromArgb(0xF0, 0xB3, 0x3A, 0x33));

        private static readonly Set ColorBlindSet = new Set(
            add: Color.FromRgb(0x4C, 0x9A, 0xFF), del: Color.FromRgb(0xE8, 0x91, 0x2D),
            addSolid: Color.FromArgb(0xF0, 0x1F, 0x6F, 0xD6), delSolid: Color.FromArgb(0xF0, 0xB8, 0x6A, 0x14));

        /// <summary>One palette with all brushes pre-built and frozen.</summary>
        internal sealed class Set
        {
            public Set(Color add, Color del, Color addSolid, Color delSolid)
            {
                AddColor = add;
                DelColor = del;
                AddText = Frozen(add);
                DeleteText = Frozen(del);
                AddFill = Frozen(WithAlpha(add, 0x33));
                AddFillDim = Frozen(WithAlpha(add, 0x12));
                AddFillStrong = Frozen(WithAlpha(add, 0x52));
                DeleteFill = Frozen(WithAlpha(del, 0x33));
                DeleteFillDim = Frozen(WithAlpha(del, 0x14));
                DeleteFillStrong = Frozen(WithAlpha(del, 0x52));
                AddFillSolid = Frozen(addSolid);
                DeleteFillSolid = Frozen(delSolid);
            }

            public Color AddColor { get; }
            public Color DelColor { get; }
            public Brush AddText { get; }
            public Brush DeleteText { get; }
            public Brush AddFill { get; }
            public Brush AddFillDim { get; }
            public Brush AddFillStrong { get; }
            public Brush DeleteFill { get; }
            public Brush DeleteFillDim { get; }
            public Brush DeleteFillStrong { get; }
            public Brush AddFillSolid { get; }
            public Brush DeleteFillSolid { get; }
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
