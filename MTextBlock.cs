
namespace AutoCADCmdAlgorithmTester
{

public sealed partial class MTextJoinerCmd
    {
        /// <summary>
        /// Представляет логический блок (абзац) текста, состоящий из одной или нескольких строк.
        /// Отслеживает границы (MinX, MaxX) и определяет, можно ли присоединить следующую строку к текущему абзацу.
        /// </summary>
        private sealed class MTextBlock
        {
            public MTextBlock(List<MTextMetrics> firstRow)
            {
                Rows.Add(firstRow);
                RefreshBounds(firstRow);
            }

            public List<List<MTextMetrics>> Rows { get; } = [];

            private double MinX { get; set; } = double.MaxValue;

            private double MaxX { get; set; } = double.MinValue;

            public double Width => MaxX - MinX;

            public bool CanAppend(List<MTextMetrics> row)
            {
                double tolerance = row.Max(t => t.Height) * BLOCK_GAP_MULTIPLIER;

                double rowMinX = row.Min(t => t.MinX);
                double rowMaxX = row.Max(t => t.MaxX);

                return rowMinX < MaxX + tolerance && rowMaxX > MinX - tolerance;
            }

            public void Append(List<MTextMetrics> row)
            {
                Rows.Add(row);
                RefreshBounds(row);
            }

            private void RefreshBounds(IEnumerable<MTextMetrics> row)
            {
                MinX = Math.Min(MinX, row.Min(t => t.MinX));
                MaxX = Math.Max(MaxX, row.Max(t => t.MaxX));
            }
        }
    }
}
