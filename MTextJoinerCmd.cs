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
    // Id - ID исходного MText.
    // RawText - Текст без формат-кодов.
    // Centroid - Центр габаритов текста.
    // TopLeftPt - Верхняя левая точка габаритов.
    // Height - Высота исходного текста.
    // LayerId - Слой исходного текста.
    // StyleId - Текстовый стиль текста.
    // Rotation - Поворот исходного текста.
    public record MTextMetrics(ObjectId Id, string RawText, Point3d Centroid, Point3d TopLeftPt, double Height, ObjectId LayerId, ObjectId StyleId, double Rotation);


    public sealed class MTextJoinerCmd
    {
        private const double Y_TOLERANCE_MULTIPLIER = 0.5; // Допуск по Y (половина высоты текста)
        private const double TAB_INSERTION_MULTIPLIER = 1.5; // Порог для вставки табуляции вместо пробела

        [CommandMethod("SmartJoinMText", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public static void SmartJoinCommand()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            ArgumentNullException.ThrowIfNull(doc, nameof(doc));
            Database db = doc.Database;
            Editor ed = doc.Editor;

            // Фильтр: разрешаем выбирать только MTEXT
            TypedValue[] filterValues = { new((int)DxfCode.Start, "MTEXT") };
            SelectionFilter selFilter = new(filterValues);

            PromptSelectionOptions pso = new()
            {
                MessageForAdding = "\nВыберите MText для интеллектуального объединения"
            };

            PromptSelectionResult psr = ed.GetSelection(pso, selFilter);

            if (psr.Status == PromptStatus.OK)
            {
                using Transaction trx = db.TransactionManager.StartTransaction();

                BlockTableRecord currentSpace = (BlockTableRecord)trx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                List<MTextMetrics> textElements = [];

                // 1. Извлекаем данные из выбранных объектов
                foreach (SelectedObject selObj in psr.Value)
                {
                    DBObject dbObject = trx.GetObject(selObj.ObjectId, OpenMode.ForRead);

                    if (dbObject is MText mText)
                    {
                        // Вычисляем CenterPoint и TopLeftPoint до создания объекта
                        Point3d centerPoint;
                        Point3d topLeftPoint;

                        if (mText.Bounds.HasValue)
                        {
                            Extents3d ext = mText.Bounds.Value;
                            centerPoint = new Point3d(
                                (ext.MaxPoint.X + ext.MinPoint.X) / 2.0,
                                (ext.MaxPoint.Y + ext.MinPoint.Y) / 2.0,
                                ext.MinPoint.Z);
                            topLeftPoint = new Point3d(ext.MinPoint.X, ext.MaxPoint.Y, ext.MinPoint.Z);
                        }
                        else
                        {
                            centerPoint = mText.Location;
                            topLeftPoint = mText.Location;
                        }

                        MTextMetrics metrics = new(
                            mText.ObjectId,
                            mText.Text,
                            centerPoint,
                            topLeftPoint,
                            mText.TextHeight,
                            mText.LayerId,
                            mText.TextStyleId,
                            mText.Rotation
                        );

                        textElements.Add(metrics);
                    }
                }

                if (textElements.Count == 0)
                {
                    trx.Abort();
                    return;
                }

                // 2. Группируем тексты по Слою и Стилю (Главный фильтр)
                var groupedTexts = textElements.GroupBy(t => new { t.LayerId, t.StyleId }).ToList();

                int createdMTextsCount = 0;

                // 3. Обрабатываем каждую группу независимо
                foreach (var group in groupedTexts)
                {
                    List<MTextMetrics> elementsInGroup = [.. group];

                    // Первичная сортировка сверху вниз (по оси Y)
                    List<MTextMetrics> sortedVertically = elementsInGroup.OrderByDescending(t => t.Centroid.Y).ToList();

                    List<List<MTextMetrics>> textRows = [];
                    List<MTextMetrics> currentRow = [sortedVertically[0]];
                    textRows.Add(currentRow);

                    // Кластеризация по строкам с использованием индивидуальной высоты текста
                    for (int i = 1; i < sortedVertically.Count; i++)
                    {
                        MTextMetrics current = sortedVertically[i];
                        MTextMetrics previous = currentRow.Last();

                        // Допуск вычисляется на основе высоты конкретного текста
                        double currentTolerance = current.Height * Y_TOLERANCE_MULTIPLIER;

                        // Если разница по Y меньше допуска, это одна строка
                        if (Math.Abs(previous.Centroid.Y - current.Centroid.Y) <= currentTolerance)
                        {
                            currentRow.Add(current);
                        }
                        else
                        {
                            // Начинаем новую строку
                            currentRow = [current];
                            textRows.Add(currentRow);
                        }
                    }

                    // 4. Сборка финального текста для группы
                    StringBuilder mTextBuilder = new();
                    Point3d finalInsertionPoint = Point3d.Origin;
                    bool isFirstRow = true;

                    foreach (List<MTextMetrics> row in textRows)
                    {
                        // Вторичная сортировка слева направо (по оси X)
                        List<MTextMetrics> sortedHorizontally = row.OrderBy(t => t.Centroid.X).ToList();

                        if (isFirstRow)
                        {
                            finalInsertionPoint = sortedHorizontally.First().TopLeftPt;
                            isFirstRow = false;
                        }
                        else
                        {
                            // Маркер нового абзаца в MText
                            _ = mTextBuilder.Append("\\P");
                        }

                        for (int idx = 0; idx < sortedHorizontally.Count; idx++)
                        {
                            _ = mTextBuilder.Append(sortedHorizontally[idx].RawText);

                            if (idx < sortedHorizontally.Count - 1)
                            {
                                MTextMetrics currentWord = sortedHorizontally[idx];
                                MTextMetrics nextWord = sortedHorizontally[idx + 1];

                                double distanceX = Math.Abs(nextWord.TopLeftPt.X - currentWord.TopLeftPt.X);

                                // Интеллектуальная вставка пробела или табуляции
                                _ = distanceX > currentWord.Height * currentWord.RawText.Length * TAB_INSERTION_MULTIPLIER
                                    ? mTextBuilder.Append("\\t")
                                    : mTextBuilder.Append(" ");
                            }
                        }
                    }

                    // 5. Создание нового объединенного MText
                    MText resultMText = new();
                    resultMText.SetDatabaseDefaults();
                    resultMText.Location = finalInsertionPoint;
                    resultMText.Contents = mTextBuilder.ToString();

                    // Берем свойства от первого элемента в группе
                    MTextMetrics template = elementsInGroup.First();
                    resultMText.TextHeight = template.Height;
                    resultMText.LayerId = template.LayerId;
                    resultMText.TextStyleId = template.StyleId;
                    resultMText.Rotation = template.Rotation;
                    resultMText.Attachment = AttachmentPoint.TopLeft;

                    _ = currentSpace.AppendEntity(resultMText);
                    trx.AddNewlyCreatedDBObject(resultMText, true);
                    createdMTextsCount++;

                    // 6. Удаляем исходные фрагменты данной группы
                    foreach (MTextMetrics el in elementsInGroup)
                    {
                        Entity? entToErase = trx.GetObject(el.Id, OpenMode.ForWrite) as Entity;
                        entToErase?.Erase();
                    }
                }

                trx.Commit();
                ed.WriteMessage($"\nГОТОВО: Из {textElements.Count} разрозненных фрагментов собрано {createdMTextsCount} правильных MText.");
            }
        }
    }
}