namespace AutoCADCmdAlgorithmTester
{
    internal sealed class MTextBlock
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

        public bool CanAppend(List<MTextMetrics> row, double blockGapMultiplier)
        {
            double tolerance = row.Max(t => t.Height) * blockGapMultiplier;

            double rowMinX = row.Min(t => t.Bounds.MinPoint.X);
            double rowMaxX = row.Max(t => t.Bounds.MaxPoint.X);

            return rowMinX < MaxX + tolerance && rowMaxX > MinX - tolerance;
        }

        public void Append(List<MTextMetrics> row)
        {
            Rows.Add(row);
            RefreshBounds(row);
        }

        private void RefreshBounds(IEnumerable<MTextMetrics> row)
        {
            MinX = Math.Min(MinX, row.Min(t => t.Bounds.MinPoint.X));
            MaxX = Math.Max(MaxX, row.Max(t => t.Bounds.MaxPoint.X));
        }
    }

}
