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
    public record MTextMetrics(
        ObjectId Id,
        string RawText,
        Point3d Centroid,
        Point3d TopLeftPt,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        double Height,
        ObjectId LayerId,
        ObjectId StyleId,
        double Rotation);

    /// <summary>
    /// Основной класс команды для интеллектуального объединения разрозненных MText в единые текстовые блоки (абзацы).
    /// Алгоритм группирует тексты по слою/стилю, затем по строкам (Y), разбивает строки на колонки (X) 
    /// и собирает пересекающиеся по горизонтали колонки в связные абзацы текста.
    /// </summary>
    public sealed partial class MTextJoinerCmd
    {
        // Множитель для определения максимального допустимого разрыва между колонками/сегментами по горизонтали
        private const double BLOCK_GAP_MULTIPLIER = 2.5;
        // Множитель для объединения текстов в одну строку (допуск по оси Y от высоты текста)
        private const double Y_TOLERANCE_MULTIPLIER = 0.5;
        // Множитель для вставки табуляции вместо пробела между фрагментами в одной строке
        private const double TAB_INSERTION_MULTIPLIER = 1.5;


        [CommandMethod("SmartJoinMText", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public static void SmartJoinCommand()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            ArgumentNullException.ThrowIfNull(doc, nameof(doc));

            Editor ed = doc.Editor;
            Database db = doc.Database;

            // 1. Запрашиваем у пользователя выбор текстов и извлекаем их метрики (координаты, габариты, слои)
            List<MTextMetrics> textElements = SelectAndExtractMTexts(ed, db);

            if (textElements.Count > 0)
            {
                int createdCount = 0;

                // 2. Группируем элементы по слою и стилю, чтобы в один абзац не попали тексты с разным оформлением
                IEnumerable<IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics>> groupedByLayerAndStyle = textElements.GroupBy(t => (t.LayerId, t.StyleId));

                using Transaction trx = db.TransactionManager.StartTransaction();

                BlockTableRecord currentSpace = (BlockTableRecord)trx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                foreach (IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics> group in groupedByLayerAndStyle)
                {
                    List<MTextMetrics> groupElements = [.. group];

                    // 3. Главный этап алгоритма: кластеризуем тексты внутри группы в логические абзацы (блоки)
                    List<MTextBlock> blocks = ClusterIntoBlocks(groupElements);

                    foreach (MTextBlock block in blocks)
                    {
                        // 4. Формируем единую строку с корректными переносами \P и отступами \t
                        string content = BuildMTextContent(block.Rows);
                        (Point3d insertionPoint, MTextMetrics template) = GetBlockAnchor(block.Rows);

                        // 5. Создаем новый объединённый MText в чертеже
                        CreateResultMText(currentSpace, trx, content, template, insertionPoint, block.Width);
                        createdCount++;
                    }

                    // 6. Удаляем исходные (разрозненные) куски текста
                    EraseOriginals(trx, groupElements);
                }

                trx.Commit();
                ed.WriteMessage($"\nГОТОВО: Из {textElements.Count} разрозненных фрагментов собрано {createdCount} правильных MText.");
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

                if (dbObj is MText mText && TryGetBounds(mText, out Point3d centroid, out Point3d topLeft, out Extents3d ext))
                {
                    result.Add(new MTextMetrics(
                        mText.ObjectId, mText.Text, centroid, topLeft,
                        ext.MinPoint.X, ext.MaxPoint.X, ext.MinPoint.Y, ext.MaxPoint.Y,
                        mText.TextHeight, mText.LayerId, mText.TextStyleId, mText.Rotation));
                }
            }

            trx.Commit();
            return result;
        }

        private static bool TryGetBounds(MText mText, out Point3d centroid, out Point3d topLeft, out Extents3d ext)
        {
            centroid = Point3d.Origin;
            topLeft = Point3d.Origin;
            ext = new Extents3d();

            if (mText.Bounds.HasValue)
            {
                ext = mText.Bounds.Value;
                centroid = new Point3d(
                    (ext.MaxPoint.X + ext.MinPoint.X) * 0.5,
                    (ext.MaxPoint.Y + ext.MinPoint.Y) * 0.5,
                    ext.MinPoint.Z);
                topLeft = new Point3d(ext.MinPoint.X, ext.MaxPoint.Y, ext.MinPoint.Z);

                return true;
            }

            return false;
        }

        /// <summary>
        /// 1-й этап нормализации: Кластеризует тексты по строкам с учетом их координаты Y (центроидов).
        /// Идет сверху вниз. Если следующий элемент попадает в допуск текущей высоты строки, 
        /// он добавляется в ту же строку, если нет — начинается новая строка.
        /// </summary>
        private static List<List<MTextMetrics>> ClusterIntoRows(List<MTextMetrics> elements)
        {
            List<MTextMetrics> sortedByY = [.. elements.OrderByDescending(t => t.Centroid.Y)];
            List<MTextMetrics> currentRow = [sortedByY[0]];
            List<List<MTextMetrics>> rows = [];

            for (int idx = 1; idx < sortedByY.Count; idx++)
            {
                MTextMetrics current = sortedByY[idx];
                MTextMetrics anchor = currentRow[0];

                double tolerance = Math.Max(current.Height, anchor.Height) * Y_TOLERANCE_MULTIPLIER;

                double distance = Math.Abs(anchor.Centroid.Y - current.Centroid.Y);

                if (distance < tolerance)
                {
                    currentRow.Add(current);
                }
                else
                {
                    rows.Add(currentRow);
                    currentRow = [current];
                }
            }

            rows.Add(currentRow);
            return rows;
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
            List<List<MTextMetrics>> rows = ClusterIntoRows(elements);
            List<MTextBlock> blocks = [];

            foreach (List<MTextMetrics> row in rows)
            {
                foreach (List<MTextMetrics> segment in SplitRowIntoSegments(row))
                {
                    MTextBlock? targetBlock = blocks.FirstOrDefault(block => block.CanAppend(segment));

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
        /// 2-й этап нормализации: Разбивает строку на несколько сегментов, если расстояние (X) 
        /// между соседними фрагментами в строке больше допустимого разрыва.
        /// Таким образом строки, визуально разбитые на разные столбцы таблицы/текста, разделяются.
        /// </summary>
        private static List<List<MTextMetrics>> SplitRowIntoSegments(List<MTextMetrics> row)
        {
            List<MTextMetrics> sortedByX = [.. row.OrderBy(t => t.MinX)];
            List<List<MTextMetrics>> segments = [];
            List<MTextMetrics> currentSegment = [sortedByX[0]];

            for (int idx = 1; idx < sortedByX.Count; idx++)
            {
                MTextMetrics previous = currentSegment[^1];
                MTextMetrics current = sortedByX[idx];

                double gap = current.MinX - previous.MaxX;
                double tolerance = previous.Height * BLOCK_GAP_MULTIPLIER;

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
        /// Вместо пробелов при больших разрывах ставится табуляция (\t), а все новые строки начинаются с символа переноса (\P).
        /// </summary>
        private static string BuildMTextContent(List<List<MTextMetrics>> rows)
        {
            bool isFirstRow = true;
            StringBuilder sb = new();

            foreach (List<MTextMetrics> row in rows)
            {
                if (!isFirstRow)
                {
                    sb.Append("\\P");
                }

                for (int idx = 0; idx < row.Count; idx++)
                {
                    sb.Append(row[idx].RawText);

                    if (idx < row.Count - 1)
                    {
                        MTextMetrics current = row[idx];
                        MTextMetrics next = row[idx + 1];

                        double distanceX = Math.Max(0, next.MinX - current.MaxX);
                        double textWidth = current.MaxX - current.MinX;

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
            MTextMetrics? topElement = null;

            foreach (MTextMetrics m in rows.SelectMany(row => row))
            {
                if (m.MinX < minX) minX = m.MinX;
                if (topElement is null || m.MaxY > topElement.MaxY || (m.MaxY == topElement.MaxY && m.MinX < topElement.MinX))
                    topElement = m;
            }

            return (new Point3d(minX, topElement!.MaxY, topElement.TopLeftPt.Z), topElement);
        }

        private static void CreateResultMText(
            BlockTableRecord currentSpace, Transaction trx,
            string content, MTextMetrics template, Point3d insertionPoint, double width)
        {
            MText result = new()
            {
                Location = insertionPoint,
                Contents = content,
                TextHeight = template.Height,
                LayerId = template.LayerId,
                TextStyleId = template.StyleId,
                Rotation = template.Rotation,
                Width = width,
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
