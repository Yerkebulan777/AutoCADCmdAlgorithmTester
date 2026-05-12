using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System.Text;

using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AutoCADCmdAlgorithmTester.MTextJoinerCmd))]
[assembly: ExtensionApplication(typeof(AutoCADCmdAlgorithmTester.PluginExtension))]

namespace AutoCADCmdAlgorithmTester
{
    internal record MTextMetrics(
        ObjectId Id,
        double Height,
        string RawText,
        Point3d Centroid,
        Extents3d Bounds,
        ObjectId LayerId,
        ObjectId StyleId,
        double Rotation);

    /// <summary>
    /// Основной класс команды для интеллектуального объединения разрозненных MText в единые текстовые блоки (абзацы).
    /// Алгоритм группирует тексты по слою/стилю, затем по строкам (Y), разбивает строки на колонки (X)
    /// и собирает пересекающиеся по горизонтали колонки в связные абзацы текста.
    /// </summary>
    internal sealed partial class MTextJoinerCmd
    {
        // Минимальный X-зазор для разделения строки на колонки.
        // Разрыв > COLUMN_GAP_MULTIPLIER × h между соседними фрагментами → новая колонка.
        private const double COLUMN_GAP_MULTIPLIER = 0.5;

        // Допуск X-перекрытия при проверке принадлежности сегмента блоку —
        // строгий, чтобы блок не «всасывал» соседнюю колонку.
        // Воронка = ±BLOCK_X_TOLERANCE_MULTIPLIER × h на каждую сторону.
        private const double BLOCK_X_TOLERANCE_MULTIPLIER = 0.2;

        // Допуск Y-близости (в единицах высоты текста) между последовательными
        // строками одного блока. Уменьшено с 3.0, чтобы пробел между абзацами не сливал их.
        private const double BLOCK_Y_TOLERANCE_MULTIPLIER = 0.8;

        // Максимальный суммарный вертикальный размах блока (в единицах высоты текста).
        // Сторожевое ограничение: колонтитул и заголовок не попадут в один блок.
        private const double BLOCK_MAX_HEIGHT_MULTIPLIER = 30.0;

        // Допуск Y-расстояния между центроидами (в единицах высоты) для того,
        // чтобы два фрагмента считались на одной горизонтальной строке.
        private const double ROW_Y_TOLERANCE_MULTIPLIER = 0.3;

        // Отношение разрыва к ширине текущего фрагмента, при превышении которого
        // вместо пробела вставляется табуляция (\t) в итоговом MText.
        private const double TAB_INSERTION_MULTIPLIER = 1.5;



        [CommandMethod("SmartJoinMText", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public static void SmartJoinCommand()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            ArgumentNullException.ThrowIfNull(doc, nameof(doc));

            Editor ed = doc.Editor;
            Database db = doc.Database;

            List<MTextMetrics> textElements = SelectAndExtractMTexts(ed, db);

            if (textElements.Count > 0)
            {
                //  Группируем элементы по слою и стилю, чтобы в один абзац не попали тексты с разным оформлением
                IEnumerable<IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics>> groupedByLayerAndStyle = textElements.GroupBy(t => (t.LayerId, t.StyleId));

                using Transaction trx = db.TransactionManager.StartTransaction();

                BlockTableRecord currentSpace = (BlockTableRecord)trx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                foreach (IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics> group in groupedByLayerAndStyle)
                {
                    List<MTextMetrics> groupElements = [.. group];

                    List<MTextBlock> blocks = ClusterIntoBlocks(groupElements);

                    foreach (MTextBlock block in blocks)
                    {
                        if (block.Rows.Count > 0)
                        {
                            // Формируем единую строку с корректными переносами \P и отступами \t
                            string content = BuildMTextContent(block.Rows);

                            (Point3d insertionPoint, MTextMetrics template) = GetBlockAnchor(block.Rows);

                            // Создаем новый объединённый MText в чертеже
                            CreateResultMText(currentSpace, trx, content, template, insertionPoint, block.Width);
                        }
                    }

                    // Удаляем исходные (разрозненные) куски текста
                    EraseOriginals(trx, groupElements);
                }

                trx.Commit();

                ed.WriteMessage($"\nГОТОВО!");
            }
        }


        private static List<MTextMetrics> SelectAndExtractMTexts(Editor ed, Database db)
        {
            TypedValue[] filterValues = [new((int)DxfCode.Start, "MTEXT")];
            SelectionFilter selFilter = new(filterValues);
            PromptSelectionOptions pso = new()
            {
                MessageForAdding = "\nВыберите MText для интеллектуального объединения"
            };

            PromptSelectionResult psr = ed.GetSelection(pso, selFilter);
            if (psr.Status != PromptStatus.OK)
            {
                return [];
            }

            using Transaction trx = db.TransactionManager.StartTransaction();

            List<MTextMetrics> result = [];

            foreach (SelectedObject selObj in psr.Value)
            {
                DBObject dbObj = trx.GetObject(selObj.ObjectId, OpenMode.ForRead);

                if (dbObj is MText mText && TryGetBounds(mText, out Point3d centroid, out Extents3d ext))
                {
                    result.Add(new MTextMetrics(
                        mText.ObjectId, mText.TextHeight, mText.Text, centroid,
                        ext, mText.LayerId, mText.TextStyleId, mText.Rotation));
                }
            }

            trx.Commit();
            return result;
        }

        private static bool TryGetBounds(MText mText, out Point3d centroid, out Extents3d ext)
        {
            ext = default;
            centroid = Point3d.Origin;

            if (mText.Bounds.HasValue)
            {
                ext = mText.Bounds.Value;
                centroid = new Point3d(
                    (ext.MaxPoint.X + ext.MinPoint.X) * 0.5,
                    (ext.MaxPoint.Y + ext.MinPoint.Y) * 0.5,
                    ext.MinPoint.Z);

                return true;
            }

            return false;
        }



        /// <summary>
        /// Главный метод кластеризации текстов в абзацы (блоки).
        /// Алгоритм:
        /// 1. Разбивает все фрагменты на горизонтальные строки.
        /// 2. Каждую строку режет на фрагменты (сегменты), если в ней есть большие разрывы.
        /// 3. "Укладывает" полученные сегменты в блоки (абзацы), проверяя, перекрывается ли сегмент с блоком по оси X.
        /// </summary>
        private static List<MTextBlock> ClusterIntoBlocks(List<MTextMetrics> elements)
        {
            List<MTextBlock> blocks = [];

            List<List<MTextMetrics>> rows = ClusterIntoRows(elements);

            foreach (List<MTextMetrics> row in rows)
            {
                foreach (List<MTextMetrics> segment in SplitRowIntoSegments(row))
                {
                    double segCenterX = (segment.Min(t => t.Bounds.MinPoint.X) + segment.Max(t => t.Bounds.MaxPoint.X)) * 0.5;

                    MTextBlock? targetBlock = blocks
                                            .Where(b => b.IsCompatible(segment, BLOCK_X_TOLERANCE_MULTIPLIER, BLOCK_Y_TOLERANCE_MULTIPLIER, BLOCK_MAX_HEIGHT_MULTIPLIER))
                        .MinBy(b => Math.Abs(b.CenterX - segCenterX));

                    if (targetBlock is null)
                    {
                        blocks.Add(new MTextBlock(segment));
                    }
                    else
                    {
                        targetBlock.Append(segment);
                    }
                }
            }

            return blocks;
        }

        /// <summary>
        /// Группирует фрагменты текста в горизонтальные строки по Y-координате центроидов.
        /// ПОЧЕМУ отслеживаем диапазон Y строки, а не сравниваем с якорем [0]:
        /// Сравнение каждого нового элемента только с currentRow[0] накапливает ошибку
        /// ("anchor drift") при переменных высотах текста. Если первый элемент строки —
        /// крупный заголовок, его большая высота создаёт широкий допуск и притягивает
        /// лишние элементы. Если высоты постепенно дрейфуют (типично в ячейках таблиц),
        /// накопленное смещение разрывает то, что должно быть единой строкой.
        /// Отслеживание текущего диапазона [rowMinCentroidY, rowMaxCentroidY] и максимальной
        /// высоты строки даёт стабильную, устойчивую к дрейфу границу.
        /// Строки выдаются в порядке сверху вниз (убывающий Y), чтобы ClusterIntoBlocks
        /// обрабатывал документ от заголовка к нижней части листа.
        /// </summary>
        private static List<List<MTextMetrics>> ClusterIntoRows(List<MTextMetrics> elements)
        {
            // Текущий Y-диапазон и максимальная высота строки — расширяются при каждом добавлении.
            List<MTextMetrics> sortedByY = [.. elements.OrderByDescending(t => t.Centroid.Y)];
            List<MTextMetrics> currentRow = [sortedByY[0]];

            List<List<MTextMetrics>> rows = [];

            double rowMaxHeight = sortedByY[0].Height;

            double rowMinCentroidY = sortedByY[0].Centroid.Y;
            double rowMaxCentroidY = sortedByY[0].Centroid.Y;

            for (int idx = 1; idx < sortedByY.Count; idx++)
            {
                MTextMetrics current = sortedByY[idx];

                // Допуск берём только от высоты текущего кандидата (консервативный подход).
                // Высота уже добавленных элементов не должна влиять на приём нового —
                // крупный заголовок не должен раздувать допуск и притягивать следующую строку.
                double tolerance = current.Height * ROW_Y_TOLERANCE_MULTIPLIER;

                // Расстояние до ближайшего края диапазона строки, а не до фиксированного якоря.
                // Если кандидат внутри [rowMinCentroidY, rowMaxCentroidY] — расстояние равно 0.
                double distanceToRange = Math.Max(0,
                    Math.Max(rowMinCentroidY - current.Centroid.Y,   // кандидат ниже диапазона
                             current.Centroid.Y - rowMaxCentroidY));  // кандидат выше диапазона

                if (distanceToRange < tolerance)
                {
                    // Кандидат принадлежит текущей строке — расширяем диапазон.
                    currentRow.Add(current);
                    rowMinCentroidY = Math.Min(rowMinCentroidY, current.Centroid.Y);
                    rowMaxCentroidY = Math.Max(rowMaxCentroidY, current.Centroid.Y);
                    rowMaxHeight = Math.Max(rowMaxHeight, current.Height);
                }
                else
                {
                    // Кандидат слишком далеко — закрываем текущую строку, начинаем новую.
                    rows.Add(currentRow);
                    currentRow = [current];
                    rowMaxHeight = current.Height;
                    rowMinCentroidY = current.Centroid.Y;
                    rowMaxCentroidY = current.Centroid.Y;
                }
            }

            rows.Add(currentRow);
            return rows;
        }

        /// <summary>
        /// Разбивает строку на несколько сегментов, если расстояние (X)
        /// между соседними фрагментами в строке больше допустимого разрыва.
        /// Таким образом строки, визуально разбитые на разные столбцы таблицы/текста, разделяются.
        /// </summary>
        private static List<List<MTextMetrics>> SplitRowIntoSegments(List<MTextMetrics> row)
        {
            List<MTextMetrics> sortedByX = [.. row.OrderBy(t => t.Bounds.MinPoint.X)];
            List<MTextMetrics> currentSegment = [sortedByX[0]];

            List<List<MTextMetrics>> segments = [];

            for (int idx = 1; idx < sortedByX.Count; idx++)
            {
                MTextMetrics previous = currentSegment[^1];
                MTextMetrics current = sortedByX[idx];

                double gap = current.Bounds.MinPoint.X - previous.Bounds.MaxPoint.X;
                double tolerance = Math.Max(previous.Height, current.Height) * COLUMN_GAP_MULTIPLIER;

                if (gap > tolerance)
                {
                    segments.Add(currentSegment);
                    currentSegment = [current];
                }
                else
                {
                    currentSegment.Add(current);
                }
            }

            segments.Add(currentSegment);
            return segments;
        }

        /// <summary>
        /// Формирует итоговый текст (с использованием спецсимволов MText).
        /// Вместо пробелов при больших разрывах ставится табуляция (\t), а все новые строки начинают с символа переноса (\P).
        /// </summary>
        private static string BuildMTextContent(List<List<MTextMetrics>> rows)
        {
            bool isFirstRow = true;
            StringBuilder sb = new();

            foreach (List<MTextMetrics> row in rows)
            {
                if (!isFirstRow)
                {
                    _ = sb.Append("\\P");
                }

                for (int idx = 0; idx < row.Count; idx++)
                {
                    _ = sb.Append(row[idx].RawText);

                    if (idx < row.Count - 1)
                    {
                        MTextMetrics current = row[idx];
                        MTextMetrics next = row[idx + 1];

                        double distanceX = Math.Max(0, next.Bounds.MinPoint.X - current.Bounds.MaxPoint.X);
                        double textWidth = current.Bounds.MaxPoint.X - current.Bounds.MinPoint.X;

                        bool isTab = distanceX > textWidth * TAB_INSERTION_MULTIPLIER;
                        _ = sb.Append(isTab ? "\\t" : " ");
                    }
                }

                if (isFirstRow)
                {
                    isFirstRow = false;
                }
            }

            return sb.ToString();
        }

        private static (Point3d InsertionPoint, MTextMetrics Template) GetBlockAnchor(List<List<MTextMetrics>> rows)
        {
            double minX = double.MaxValue;
            var elements = rows.SelectMany(row => row);
            MTextMetrics topElement = elements.First();

            foreach (MTextMetrics item in elements)
            {
                Point3d itemTop = item.Bounds.MaxPoint;
                Point3d itemBottom = item.Bounds.MinPoint;
                Point3d topElementTop = topElement.Bounds.MaxPoint;
                Point3d topElementBottom = topElement.Bounds.MinPoint;

                // Выбираем элемент с наименьшей X-координатой нижней границы.
                if (itemBottom.X < minX)
                {
                    minX = itemBottom.X;
                }

                // Выбираем элемент с наибольшей Y-координатой верхней границы.
                if (itemTop.Y > topElementTop.Y)
                {
                    topElement = item;
                }

                // Если верхние границы по Y совпадают, то выбираем элемент, у которого нижняя граница находится левее.
                if (itemTop.Y == topElementTop.Y && itemBottom.X < topElementBottom.X)
                {
                    topElement = item;
                }
            }

            Point3d insertionPoint = new(minX, topElement.Bounds.MaxPoint.Y, topElement.Centroid.Z);

            return (insertionPoint, topElement);
        }

        private static void CreateResultMText(BlockTableRecord currentSpace, Transaction trx, string content, MTextMetrics template, Point3d insertionPoint, double width)
        {
            MText result = new()
            {
                Width = width,
                Contents = content,
                Location = insertionPoint,
                LayerId = template.LayerId,
                TextHeight = template.Height,
                TextStyleId = template.StyleId,
                Rotation = template.Rotation,
                Attachment = AttachmentPoint.TopLeft
            };

            result.SetDatabaseDefaults();

            _ = currentSpace.AppendEntity(result);

            trx.AddNewlyCreatedDBObject(result, true);
        }

        private static void EraseOriginals(Transaction trx, List<MTextMetrics> elements)
        {
            foreach (MTextMetrics el in elements)
            {
                if (trx.GetObject(el.Id, OpenMode.ForWrite) is Entity ent)
                {
                    ent.Erase();
                }
            }
        }
    }
}
