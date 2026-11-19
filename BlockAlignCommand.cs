using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEP_Align_Round_Duct_3D_ver1
{
    internal sealed partial class CadReader
    {
        private sealed class OriginHit
        {
            public Transform Frame;
            public string Path;
            public bool Exact;
            public int Depth;
        }

        public Transform ReadBlockFrame(ImportInstance cad, Reference reference)
        {
            picked = reference;
            pickedStable = reference.ConvertToStableRepresentation(doc);
            primitives = 0;
            var hits = new List<OriginHit>();
            var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            using (var geometry = cad.get_Geometry(options))
                FindOrigins(geometry, Transform.Identity, "", 0, hits);
            var candidates = hits.Any(h => h.Exact) ? hits.Where(h => h.Exact).ToList() : hits;
            var owners = candidates.GroupBy(h => h.Path).Select(g => g.First()).ToList();
            if (owners.Count != 1)
                throw new InvalidOperationException(owners.Count == 0
                    ? "Khong tim duoc block tai diem click. Hay click mat/canh block trong view 3D."
                    : "Diem click cham nhieu block. Hay click lai vao mat/canh rieng cua block can chon.");
            if (owners[0].Depth < 2)
                throw new InvalidOperationException("Hinh hoc nay nam truc tiep trong DWG, khong con block con. Khong dung goc toan bo CAD de can family.");
            return owners[0].Frame;
        }

        private void FindOrigins(IEnumerable<GeometryObject> geometry, Transform transform, string path,
            int depth, List<OriginHit> hits)
        {
            if (geometry == null) return;
            int index = 0;
            foreach (var obj in geometry)
            {
                string childPath = path + "/" + index++;
                if (++primitives >= Limit || depth > 64)
                    throw new InvalidOperationException("Vuot gioi han doc block CAD.");
                var instance = obj as GeometryInstance;
                if (instance != null)
                {
                    using (var symbol = instance.GetSymbolGeometry())
                    {
                        var child = transform.Multiply(instance.Transform);
                        if (ContainsHit(symbol.GetBoundingBox(), child))
                            FindOrigins(symbol, child, childPath, depth + 1, hits);
                    }
                }
                else
                {
                    bool exact;
                    if (RectPrimitiveHit(obj, transform, out exact))
                        hits.Add(new OriginHit { Frame = transform, Path = path, Exact = exact, Depth = depth });
                }
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BlockAlignCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui == null) return Result.Cancelled;
            var doc = ui.Document;
            var selection = ui.Selection.GetElementIds();
            try
            {
                var cadRef = ui.Selection.PickObject(ObjectType.PointOnElement, new BlockFilter(),
                    "Block Align: click mat/canh block CAD (lay block con gan nhat)");
                if (cadRef.GlobalPoint == null) throw new InvalidOperationException("Khong doc duoc diem click CAD.");
                var cad = doc.GetElement(cadRef.ElementId) as ImportInstance;
                var frame = new CadReader(doc, null, cadRef.GlobalPoint).ReadBlockFrame(cad, cadRef);
                var familyRef = ui.Selection.PickObject(ObjectType.Element, new FamilyFilter(),
                    "Block Align: chon family de can goc XYZ, giu nguyen goc xoay");
                var family = (FamilyInstance)doc.GetElement(familyRef.ElementId);
                MoveToOrigin(data.Application, doc, family, frame.Origin);
                ui.RefreshActiveView();
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Block Align", "Khong giu thay doi align.\n\n" + ex.Message);
                return Result.Cancelled;
            }
            finally { try { ui.Selection.SetElementIds(selection); } catch { } }
        }

        internal static void MoveToOrigin(UIApplication app, Document doc, FamilyInstance family, XYZ target)
        {
            if (family.Pinned || family.GroupId != ElementId.InvalidElementId || family.SuperComponent != null)
                throw new InvalidOperationException("Family dang pin, thuoc group hoac la family long. Hay chon family doc lap co the di chuyen.");
            var before = family.GetTransform();
            var delta = target - before.Origin;
            if (delta.GetLength() < Units.Ft(0.01)) return;
            var manager = family.MEPModel == null ? null : family.MEPModel.ConnectorManager;
            if (manager != null && manager.Connectors.Cast<Connector>().Any(c =>
                c.ConnectorType != ConnectorType.Logical && c.Domain != Domain.DomainHvac &&
                c.Domain != Domain.DomainPiping && c.IsConnected))
                throw new InvalidOperationException("Chi ho tro ket noi duct/pipe; family co ket noi vat ly khac.");
            var guard = RectConnectionGuard.CaptureFamily(doc, family);
            guard.Prepare(app, doc, delta);
            var added = new HashSet<int>();
            var deleted = new HashSet<int>();
            EventHandler<DocumentChangedEventArgs> observer = (sender, args) =>
            {
                if (!args.GetDocument().Equals(doc)) return;
                foreach (var id in args.GetAddedElementIds()) added.Add(id.IntegerValue);
                foreach (var id in args.GetDeletedElementIds()) deleted.Add(id.IntegerValue);
            };
            using (var group = new TransactionGroup(doc, "Block Align - family origin to CAD"))
            {
                group.Start();
                try
                {
                    app.Application.DocumentChanged += observer;
                    var failures = new FailureCapture();
                    using (var tx = new Transaction(doc, "Move family origin XYZ"))
                    {
                        tx.Start();
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                            .SetClearAfterRollback(true).SetFailuresPreprocessor(failures));
                        ElementTransformUtils.MoveElements(doc, guard.MoveIds, delta);
                        doc.Regenerate();
                        if (tx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Revit khong cho phep di chuyen family.\n" + string.Join("\n", failures.Messages));
                    }
                    guard.Validate(doc);
                    if (guard.HasPhysicalChanges(doc, added, deleted))
                        throw new InvalidOperationException("Revit them/xoa phan tu vat ly khi di chuyen. Tool se rollback.");
                    var actual = ((FamilyInstance)doc.GetElement(family.Id)).GetTransform();
                    if (actual.Origin.DistanceTo(target) > Units.Ft(0.1) ||
                        !actual.BasisX.IsAlmostEqualTo(before.BasisX) ||
                        !actual.BasisY.IsAlmostEqualTo(before.BasisY) ||
                        !actual.BasisZ.IsAlmostEqualTo(before.BasisZ))
                        throw new InvalidOperationException("Family bi rang buoc: goc XYZ hoac goc xoay khong dung yeu cau. Da huy di chuyen.");
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Khong the hoan tat Block Align.");
                }
                finally
                {
                    app.Application.DocumentChanged -= observer;
                    if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                }
            }
        }

        private sealed class BlockFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) { return e is ImportInstance; }
            public bool AllowReference(Reference r, XYZ p) { return true; }
        }
        private sealed class FamilyFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) { return e is FamilyInstance; }
            public bool AllowReference(Reference r, XYZ p) { return false; }
        }
    }
}
