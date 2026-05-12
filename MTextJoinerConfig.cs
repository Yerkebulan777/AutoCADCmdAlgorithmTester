namespace AutoCADCmdAlgorithmTester
{
    internal static class MTextJoinerConfig
    {
        // Минимальный X-зазор для разделения строки на колонки.
        // Разрыв > ColumnGapMultiplier × h между соседними фрагментами → новая колонка.
        public const double ColumnGapMultiplier = 0.5;

        // Допуск X-перекрытия при проверке принадлежности сегмента блоку —
        // строгий, чтобы блок не «всасывал» соседнюю колонку.
        public const double BlockXToleranceMultiplier = 0.2;

        // Допуск Y-близости (в единицах высоты текста) между последовательными
        // строками одного блока. Уменьшено с 3.0, чтобы пробел между абзацами не сливал их.
        public const double BlockYToleranceMultiplier = 1.0;

        // Максимальный суммарный вертикальный размах блока (в единицах высоты текста).
        // Сторожевое ограничение: колонтитул и заголовок не попадут в один блок.
        public const double BlockMaxHeightMultiplier = 30.0;

        // Допуск Y-расстояния между центроидами (в единицах высоты) для того,
        // чтобы два фрагмента считались на одной горизонтальной строке.
        public const double RowYToleranceMultiplier = 0.3;

        // Отношение разрыва к ширине текущего фрагмента, при превышении которого
        // вместо пробела вставляется табуляция (\t) в итоговом MText.
        public const double TabInsertionMultiplier = 1.5;

        // Защита от аномального текста с нулевой/почти нулевой высотой.
        public const double MinTextHeight = 0.01;

        // Минимальное перекрытие X-диапазонов (доля от ширины меньшего из двух).
        // Исключает ложные касания на границе соседних блоков.
        public const double MinXOverlapFraction = 0.2;
    }
}
