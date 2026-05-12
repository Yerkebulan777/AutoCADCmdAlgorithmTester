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

    public sealed class MTextJoinerCmd
    {
        private const double Y_TOLERANCE_MULTIPLIER = 0.5;
        private const double TAB_INSERTION_MULTIPLIER = 1.5;
        private const double BLOCK_GAP_MULTIPLIER = 2.5;

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

            public bool CanAppend(List<MTextMetrics> row)
            {
                double tolerance = row.Max(t => t.Height) * BLOCK_GAP_MULTIPLIER;
                double rowMinX = row.Min(t => t.MinX);
                double rowMaxX = row.Max(t => t.MaxX);

                return rowMinX <= MaxX + tolerance && rowMaxX >= MinX - tolerance;
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
                int createdCount = 0;

                IEnumerable<IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics>> groupedByLayerAndStyle = textElements.GroupBy(t => (t.LayerId, t.StyleId));

                using Transaction trx = db.TransactionManager.StartTransaction();

                BlockTableRecord currentSpace = (BlockTableRecord)trx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                foreach (IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics> group in groupedByLayerAndStyle)
                {
                    List<MTextMetrics> groupElements = [.. group];

                    List<MTextBlock> blocks = ClusterIntoBlocks(groupElements);

                    foreach (MTextBlock block in blocks)
                    {
                        string content = BuildMTextContent(block.Rows);
                        Point3d insertionPoint = GetInsertionPoint(block.Rows);
                        MTextMetrics template = GetTemplate(block.Rows);

                        CreateResultMText(currentSpace, trx, content, template, insertionPoint);
                        createdCount++;
                    }

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

        private static List<List<MTextMetrics>> ClusterIntoRows(List<MTextMetrics> elements)
        {
            List<MTextMetrics> sortedByY = [.. elements.OrderByDescending(t => t.Centroid.Y)];
            List<MTextMetrics> currentRow = [sortedByY[0]];
            List<List<MTextMetrics>> rows = [];

            for (int idx = 1; idx < sortedByY.Count; idx++)
            {
                MTextMetrics current = sortedByY[idx];
                MTextMetrics previous = currentRow[^1];

                double tolerance = current.Height * Y_TOLERANCE_MULTIPLIER;

                double distance = Math.Abs(previous.Centroid.Y - current.Centroid.Y);

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

        private static string BuildMTextContent(List<List<MTextMetrics>> rows)
        {
            bool isFirstRow = true;
            StringBuilder sb = new();

            foreach (List<MTextMetrics> row in rows)
            {
                List<MTextMetrics> sortedByX = [.. row.OrderBy(t => t.MinX)];

                if (!isFirstRow)
                {
                    sb.Append("\\P"); // Добавляется перенос строки перед каждым новым рядом текста
                }

                for (int idx = 0; idx < sortedByX.Count; idx++)
                {
                    sb.Append(sortedByX[idx].RawText);

                    if (idx < sortedByX.Count - 1)
                    {
                        MTextMetrics current = sortedByX[idx];
                        MTextMetrics next = sortedByX[idx + 1];

                        double distanceX = Math.Max(0, next.MinX - current.MaxX);

                        bool isTab = distanceX > current.Height * current.RawText.Length * TAB_INSERTION_MULTIPLIER;
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

        private static Point3d GetInsertionPoint(List<List<MTextMetrics>> rows)
        {
            double minX = rows.SelectMany(row => row).Min(t => t.MinX);
            MTextMetrics topElement = rows
                .SelectMany(row => row)
                .OrderByDescending(t => t.MaxY)
                .ThenBy(t => t.MinX)
                .First();

            return new Point3d(minX, topElement.MaxY, topElement.TopLeftPt.Z);
        }

        private static MTextMetrics GetTemplate(List<List<MTextMetrics>> rows)
        {
            return rows
                .SelectMany(row => row)
                .OrderByDescending(t => t.MaxY)
                .ThenBy(t => t.MinX)
                .First();
        }

        private static void CreateResultMText(
            BlockTableRecord currentSpace, Transaction trx,
            string content, MTextMetrics template, Point3d insertionPoint)
        {
            MText result = new()
            {
                Location = insertionPoint,
                Contents = content,
                TextHeight = template.Height,
                LayerId = template.LayerId,
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
