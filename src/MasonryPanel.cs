// MasonryPanel.cs — плотная упаковка карточек РАЗНОЙ ширины и высоты (skyline / bottom-left).
// Карточка ставится в самую верхнюю-левую позицию, куда она помещается → нет «дыр» как у WrapPanel.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace LersMonitor
{
    public class MasonryPanel : Panel
    {
        private const double Unit = 4;   // гранулярность сетки по X, px

        public static readonly DependencyProperty SpacingProperty =
            DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(MasonryPanel),
                new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty MaxCardWidthProperty =
            DependencyProperty.Register(nameof(MaxCardWidth), typeof(double), typeof(MasonryPanel),
                new FrameworkPropertyMetadata(360.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
        public double MaxCardWidth { get => (double)GetValue(MaxCardWidthProperty); set => SetValue(MaxCardWidthProperty, value); }

        protected override Size MeasureOverride(Size available)
        {
            double w = double.IsInfinity(available.Width) ? 1200 : available.Width;
            foreach (UIElement c in InternalChildren)
                c.Measure(new Size(Math.Min(MaxCardWidth, w), double.PositiveInfinity));
            var (_, size) = Pack(w);
            return new Size(Math.Min(size.Width, w), size.Height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var (rects, size) = Pack(final.Width);
            for (int i = 0; i < rects.Count; i++)
                if (rects[i] is Rect r) InternalChildren[i].Arrange(r);
            return new Size(final.Width, size.Height);
        }

        private (List<Rect?> rects, Size size) Pack(double totalWidth)
        {
            int units = Math.Max(1, (int)Math.Floor(totalWidth / Unit));
            var heights = new double[units];
            var rects = new List<Rect?>(InternalChildren.Count);
            double sp = Spacing, maxW = 0;

            foreach (UIElement c in InternalChildren)
            {
                if (c.Visibility == Visibility.Collapsed) { rects.Add(null); continue; }
                var d = c.DesiredSize;
                int w = Math.Min(units, (int)Math.Ceiling((d.Width + sp) / Unit));

                int bestX = 0; double bestY = double.MaxValue;
                for (int x = 0; x + w <= units; x++)
                {
                    double y = 0;
                    for (int k = x; k < x + w; k++) if (heights[k] > y) y = heights[k];
                    if (y < bestY - 0.01) { bestY = y; bestX = x; }      // строго меньше → при равенстве остаётся левее
                }
                if (bestY == double.MaxValue) bestY = heights.Max();

                rects.Add(new Rect(bestX * Unit, bestY, d.Width, d.Height));
                double newH = bestY + d.Height + sp;
                for (int k = bestX; k < bestX + w && k < units; k++) heights[k] = newH;
                maxW = Math.Max(maxW, bestX * Unit + d.Width);
            }
            return (rects, new Size(maxW, Math.Max(0, heights.Max() - sp)));
        }
    }
}
