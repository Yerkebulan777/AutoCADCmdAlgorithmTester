namespace AutoCADCmdAlgorithmTester
{
    /// <summary>
    /// Логический абзац: одна или несколько строк текста, которые принадлежат
    /// одному блоку, потому что они совпадают по горизонтали И смежны вертикально.
    ///
    /// Решение об объединении (IsCompatible) требует выполнения ОБОИХ условий:
    ///   X — горизонтальный диапазон строки перекрывается с диапазоном блока (с допуском).
    ///   Y — верхняя граница строки не слишком далеко ниже нижней границы последней строки блока.
    ///       Это предотвращает слияние колонтитула (Y=50) с заголовком (Y=500),
    ///       которые случайно оказались в одном диапазоне X.
    ///
    /// Система координат AutoCAD: Y растёт вверх, поэтому
    ///   верх  = Bounds.MaxPoint.Y (большее значение)
    ///   низ   = Bounds.MinPoint.Y (меньшее значение)
    /// </summary>
    internal sealed class MTextBlock(List<MTextMetrics> firstRow)
    {
        public List<List<MTextMetrics>> Rows { get; } = [firstRow];

        private double MinX { get; set; } = firstRow.Min(t => t.Bounds.MinPoint.X);
        private double MaxX { get; set; } = firstRow.Max(t => t.Bounds.MaxPoint.X);
        private double MinY { get; set; } = firstRow.Min(t => t.Bounds.MinPoint.Y);
        private double MaxY { get; set; } = firstRow.Max(t => t.Bounds.MaxPoint.Y);

        // Нижняя Y-граница последней добавленной строки.
        // Используется в IsCompatible для измерения вертикального разрыва до следующей строки-кандидата.
        private double LastRowMinY { get; set; } = firstRow.Min(t => t.Bounds.MinPoint.Y);

        /// <summary>
        /// Ширина блока. Используется при создании итогового MText в AutoCAD.
        /// </summary>
        public double Width => MaxX - MinX;

        /// <summary>
        /// Определяет, можно ли добавить <paramref name="row"/> в этот блок.
        ///
        /// Требуются ОБА условия:
        ///
        /// 1. X-пересечение (принадлежность к одной колонке):
        ///    Горизонтальный диапазон строки должен перекрываться с диапазоном блока
        ///    с допуском blockGapMultiplier × высота_текста.
        ///
        /// 2. Y-близость (вертикальная смежность):
        ///    Верх входящей строки должен быть не ниже
        ///    (нижняя граница последней строки блока − высота_текста × rowGapMultiplier).
        ///    В координатах AutoCAD (Y вверх): rowTopY >= LastRowMinY - maxHeight * rowGapMultiplier.
        /// </summary>
        /// <param name="row">Строка-кандидат (уже отделённый сегмент горизонтальной строки).</param>
        /// <param name="blockGapMultiplier">
        ///     Допуск для проверки X-пересечения, в единицах высоты текста.
        ///     Определяет ширину "воронки" одной колонки.
        /// </param>
        /// <param name="rowGapMultiplier">
        ///     Допуск для проверки Y-близости, в единицах высоты текста.
        ///     Определяет, насколько большой вертикальный разрыв ещё считается "одним абзацем".
        /// </param>
        public bool IsCompatible(List<MTextMetrics> row, double blockGapMultiplier, double rowGapMultiplier)
        {
            double maxHeight = row.Max(t => t.Height);

            // --- Проверка пересечения по X ---
            double rowMinX = row.Min(t => t.Bounds.MinPoint.X);
            double rowMaxX = row.Max(t => t.Bounds.MaxPoint.X);
            double xTolerance = maxHeight * blockGapMultiplier;

            bool xOverlaps = rowMinX < MaxX + xTolerance && rowMaxX > MinX - xTolerance;

            // --- Проверка близости по Y ---
            // Верх входящей строки (наибольший Y = самая высокая точка в системе Y-вверх).
            double rowTopY = row.Max(t => t.Bounds.MaxPoint.Y);
            double yTolerance = maxHeight * rowGapMultiplier;

            // Верх новой строки должен быть выше "пола": (низ последней строки − допуск).
            // Если разрыв слишком большой — это уже другой раздел чертежа, а не следующая строка.
            bool yIsClose = rowTopY >= LastRowMinY - yTolerance;

            return xOverlaps && yIsClose;
        }

        /// <summary>
        /// Добавляет строку в блок и расширяет границы.
        /// Вызывать только после того, как IsCompatible вернул true.
        /// </summary>
        public void Append(List<MTextMetrics> row)
        {
            Rows.Add(row);
            RefreshBounds(row);
        }

        /// <summary>
        /// Расширяет MinX/MaxX/MinY/MaxY до охвата переданной строки
        /// и запоминает её нижнюю Y-границу в LastRowMinY для следующей проверки IsCompatible.
        /// Материализуем IEnumerable один раз, чтобы не перечислять его пять раз.
        /// </summary>
        private void RefreshBounds(IEnumerable<MTextMetrics> row)
        {
            MinX = Math.Min(MinX, row.Min(t => t.Bounds.MinPoint.X));
            MaxX = Math.Max(MaxX, row.Max(t => t.Bounds.MaxPoint.X));
            MinY = Math.Min(MinY, row.Min(t => t.Bounds.MinPoint.Y));
            MaxY = Math.Max(MaxY, row.Max(t => t.Bounds.MaxPoint.Y));
            // Минимум только последней добавленной строки
            LastRowMinY = row.Min(t => t.Bounds.MinPoint.Y);
        }
    }
}
