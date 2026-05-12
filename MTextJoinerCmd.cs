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
                // Группируем по слою и стилю, чтобы в один абзац не попали тексты с разным оформлением.
                IEnumerable<IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics>> groups = textElements.GroupBy(t => (t.LayerId, t.StyleId));

                using Transaction trx = db.TransactionManager.StartTransaction();

                BlockTableRecord currentSpace = (BlockTableRecord)trx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                foreach (IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics> group in groups)
                {
                    List<MTextMetrics> groupElements = [.. group];
                    List<MTextBlock> blocks = ClusterIntoBlocks(groupElements);

                    foreach (MTextBlock block in blocks)
                    {
                        if (block.Rows.Count != 0)
                        {
                            string content = BuildMTextContent(block.Rows);
                            (Point3d insertionPoint, MTextMetrics template) = GetBlockAnchor(block.Rows);
                            CreateResultMText(currentSpace, trx, content, template, insertionPoint, block.Width);
                        }
                    }

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

            if (!mText.Bounds.HasValue)
            {
                return false;
            }

            ext = mText.Bounds.Value;
            centroid = new Point3d(
                (ext.MaxPoint.X + ext.MinPoint.X) * 0.5,
                (ext.MaxPoint.Y + ext.MinPoint.Y) * 0.5,
                ext.MinPoint.Z);

            return true;
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

            foreach (List<MTextMetrics> row in ClusterIntoRows(elements))
            {
                foreach (List<MTextMetrics> segment in SplitRowIntoSegments(row))
                {
                    double segCenterX = (segment.Min(t => t.Bounds.MinPoint.X) + segment.Max(t => t.Bounds.MaxPoint.X)) * 0.5;

                    MTextBlock? target = blocks
                        .Where(b => b.IsCompatible(segment, MTextJoinerConfig.BlockXToleranceMultiplier, MTextJoinerConfig.BlockYToleranceMultiplier))
                        .MinBy(b => Math.Abs(b.CenterX - segCenterX));

                    if (target is null)
                    {
                        blocks.Add(new MTextBlock(segment));
                    }
                    else
                    {
                        target.Append(segment);
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
            List<MTextMetrics> sortedByY = [.. elements.OrderByDescending(t => t.Centroid.Y)];
            List<MTextMetrics> currentRow = [sortedByY[0]];
            List<List<MTextMetrics>> rows = [];

            double rowMinCentroidY = sortedByY[0].Centroid.Y;
            double rowMaxCentroidY = sortedByY[0].Centroid.Y;

            for (int idx = 1; idx < sortedByY.Count; idx++)
            {
                MTextMetrics current = sortedByY[idx];

                // Допуск берём только от высоты текущего кандидата (консервативный подход).
                // Высота уже добавленных элементов не должна влиять на приём нового —
                // крупный заголовок не должен раздувать допуск и притягивать следующую строку.
                double tolerance = current.Height * MTextJoinerConfig.RowYToleranceMultiplier;

                // Расстояние до ближайшего края диапазона строки, а не до фиксированного якоря.
                // Если кандидат внутри [rowMinCentroidY, rowMaxCentroidY] — расстояние равно 0.
                double distanceToRange = Math.Max(0,
                    Math.Max(rowMinCentroidY - current.Centroid.Y,   // кандидат ниже диапазона
                             current.Centroid.Y - rowMaxCentroidY));  // кандидат выше диапазона

                if (distanceToRange < tolerance)
                {
                    currentRow.Add(current);
                    rowMinCentroidY = Math.Min(rowMinCentroidY, current.Centroid.Y);
                    rowMaxCentroidY = Math.Max(rowMaxCentroidY, current.Centroid.Y);
                }
                else
                {
                    rows.Add(currentRow);
                    currentRow = [current];
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
                double tolerance = Math.Max(previous.Height, current.Height) * MTextJoinerConfig.ColumnGapMultiplier;

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
            StringBuilder sb = new();

            for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
            {
                if (rowIdx > 0)
                {
                    _ = sb.Append("\\P");
                }

                List<MTextMetrics> row = rows[rowIdx];
                for (int idx = 0; idx < row.Count; idx++)
                {
                    _ = sb.Append(row[idx].RawText);

                    if (idx < row.Count - 1)
                    {
                        double distanceX = Math.Max(0, row[idx + 1].Bounds.MinPoint.X - row[idx].Bounds.MaxPoint.X);
                        double textWidth = row[idx].Bounds.MaxPoint.X - row[idx].Bounds.MinPoint.X;
                        _ = sb.Append(distanceX > textWidth * MTextJoinerConfig.TabInsertionMultiplier ? "\\t" : " ");
                    }
                }
            }

            return sb.ToString();
        }

        private static (Point3d InsertionPoint, MTextMetrics Template) GetBlockAnchor(List<List<MTextMetrics>> rows)
        {
            IEnumerable<MTextMetrics> elements = rows.SelectMany(row => row);

            double minX = elements.Min(t => t.Bounds.MinPoint.X);

            MTextMetrics topElement = elements
                .OrderByDescending(t => t.Bounds.MaxPoint.Y)
                .ThenBy(t => t.Bounds.MinPoint.X)
                .First();

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
