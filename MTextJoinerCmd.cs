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
        double Height,
        ObjectId LayerId,
        ObjectId StyleId,
        double Rotation);

    public sealed class MTextJoinerCmd
    {
        private const double Y_TOLERANCE_MULTIPLIER = 0.5;
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
                int createdCount = 0;

                IEnumerable<IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics>> groupedByLayerAndStyle = textElements.GroupBy(t => (t.LayerId, t.StyleId));

                using Transaction trx = db.TransactionManager.StartTransaction();

                BlockTableRecord currentSpace = (BlockTableRecord)trx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                foreach (IGrouping<(ObjectId LayerId, ObjectId StyleId), MTextMetrics> group in groupedByLayerAndStyle)
                {
                    List<MTextMetrics> groupElements = [.. group];

                    List<List<MTextMetrics>> rows = ClusterIntoRows(groupElements);

                    string content = BuildMTextContent(rows);

                    MTextMetrics template = groupElements[0];

                    CreateResultMText(currentSpace, trx, content, template);
                    EraseOriginals(trx, groupElements);

                    createdCount++;
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

                if (dbObj is MText mText && TryGetBounds(mText, out Point3d centroid, out Point3d topLeft))
                {
                    result.Add(new MTextMetrics(
                        mText.ObjectId, mText.Text, centroid, topLeft,
                        mText.TextHeight, mText.LayerId, mText.TextStyleId, mText.Rotation));
                }
            }

            trx.Commit();
            return result;
        }

        private static bool TryGetBounds(MText mText, out Point3d centroid, out Point3d topLeft)
        {
            centroid = Point3d.Origin;
            topLeft = Point3d.Origin;

            if (mText.Bounds.HasValue)
            {
                Extents3d ext = mText.Bounds.Value;
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

        private static string BuildMTextContent(List<List<MTextMetrics>> rows)
        {
            bool isFirstRow = true;
            StringBuilder sb = new();
            Point3d insertionPoint = Point3d.Origin;

            foreach (List<MTextMetrics> row in rows)
            {
                List<MTextMetrics> sortedByX = [.. row.OrderBy(t => t.Centroid.X)];

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

                        double distanceX = Math.Abs(next.TopLeftPt.X - current.TopLeftPt.X);

                        bool isTab = distanceX > current.Height * current.RawText.Length * TAB_INSERTION_MULTIPLIER;
                        _ = sb.Append(isTab ? "\\t" : " ");
                    }
                }

                if (isFirstRow)
                {
                    insertionPoint = sortedByX[0].TopLeftPt;
                    isFirstRow = false;
                }
            }

            return sb.ToString();
        }

        private static void CreateResultMText(
            BlockTableRecord currentSpace, Transaction trx,
            string content, MTextMetrics template)
        {
            MText result = new()
            {
                Location = template.TopLeftPt,
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
