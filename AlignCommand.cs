using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEP_Align_Round_Duct_3D_ver1
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AlignDuctCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        { return ExecuteForShape(data, ref message, true); }

        internal static Result ExecuteForShape(ExternalCommandData data, ref string message, bool round)
        {
            UIDocument ui = data.Application.ActiveUIDocument;
            if (ui == null) return Result.Cancelled;
            Document doc = ui.Document;
            ICollection<ElementId> oldSelection = ui.Selection.GetElementIds();
            try
            {
                if (doc.IsReadOnly || doc.IsFamilyDocument || doc.IsModifiable)
                    throw new InvalidOperationException("Hay mo project va ket thuc lenh dang chay truoc khi align.");
                Reference dr = ui.Selection.PickObject(ObjectType.Element, new DuctFilter(round),
                    round ? "Chon ong gio TRON thang trong Revit. Esc: huy." : "Chon ong gio CHU NHAT thang trong Revit. Esc: huy.");
                Duct duct = doc.GetElement(dr.ElementId) as Duct;
                DuctFrame frame = DuctFrame.Create(duct);
                if (frame.Round != round) throw new InvalidOperationException("Tiet dien duct khong phu hop voi lenh da chon.");
                if (duct.Pinned || duct.GroupId != ElementId.InvalidElementId)
                    throw new InvalidOperationException("Duct dang Pin hoac thuoc Group. Hay xu ly rang buoc truoc.");
                Reference cr = ui.Selection.PickObject(ObjectType.PointOnElement, new CadFilter(),
                    round ? "Bam BE MAT/NET CAD gan doan ong dich. KHONG can bam duong tim. Esc: huy." :
                    "Click vao THAN BLOCK DUCT CAD; tool tu tim center line trong block. Khong can pick net tim. Esc: huy.");
                ImportInstance cad = doc.GetElement(cr.ElementId) as ImportInstance;
                if (cr.GlobalPoint == null) throw new InvalidOperationException("Khong doc duoc diem bam CAD. Thu trong view 3D.");
                CadReader reader = new CadReader(doc, frame, cr.GlobalPoint);
                List<AxisCandidate> candidates = reader.Read(cad, cr);
                if (candidates.Count == 0)
                {
                    TaskDialog.Show("Khong xac dinh duoc center line khoi da pick", "Khoi tai diem pick khong co truc ong thang cung tiet dien/song song voi duct.\n" +
                        (round ? "Hay pick be mat hoac canh THAN khoi CAD. Mot net roi khong du de xac dinh khoi.\n\n" :
                        "Click than block duct co khoi chu nhat cung size hoac center line tren layer CENTER/CENTRE/CL/TIM. Neu CAD bi flatten mat cau truc block, chi nhan tim khoi duoc click.\n\n") + reader.Diagnostics);
                    return Result.Cancelled;
                }
                if (candidates.Count != 1)
                    throw new InvalidOperationException("Diem pick cham nhieu truc CAD. Hay pick lai giua than mot khoi de xac dinh duy nhat center line.");
                return ApplyAlignment(data.Application, ui, duct, frame, candidates[0]);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Align duct / CAD", "Khong giu thay doi align.\n\n" + ex.Message);
                return Result.Cancelled;
            }
            finally { try { ui.Selection.SetElementIds(oldSelection); } catch { } }
        }

        private static Result ApplyAlignment(UIApplication app, UIDocument ui, Duct duct, DuctFrame frame, AxisCandidate target)
        {
            const bool xyOnly = false;
            Document doc = ui.Document;
            XYZ delta = target.Translation(frame, xyOnly);
            if (target.Residual(frame, delta, xyOnly) > Units.Ft(1))
                throw new InvalidOperationException("Sai so sau dich > 1 mm. Ban manual khong xoay/keo dai duct.");
            if (delta.GetLength() < Units.Ft(0.01))
                return Result.Succeeded;
            string originalRefs = Connections(duct);
            RectConnectionGuard rectGuard = frame.Round ? null : RectConnectionGuard.Capture(doc, duct);
            if (rectGuard != null) rectGuard.Prepare(app, doc, delta);
            HashSet<int> modified = new HashSet<int>(), added = new HashSet<int>(), deleted = new HashSet<int>();
            EventHandler<DocumentChangedEventArgs> observer = (sender, args) =>
            {
                if (!args.GetDocument().Equals(doc)) return;
                foreach (ElementId id in args.GetModifiedElementIds()) modified.Add(id.IntegerValue);
                foreach (ElementId id in args.GetAddedElementIds()) added.Add(id.IntegerValue);
                foreach (ElementId id in args.GetDeletedElementIds()) deleted.Add(id.IntegerValue);
            };
            using (TransactionGroup group = new TransactionGroup(doc, "Align duct to CAD - manual"))
            {
                group.Start();
                bool kept = false;
                try
                {
                    FailureCapture failures = new FailureCapture();
                    TransactionStatus status;
                    app.Application.DocumentChanged += observer;
                    try
                    {
                        using (Transaction tx = new Transaction(doc, "Align duct to picked CAD body"))
                        {
                            tx.Start();
                            FailureHandlingOptions options = tx.GetFailureHandlingOptions();
                            options.SetClearAfterRollback(true);
                            options.SetFailuresPreprocessor(failures);
                            tx.SetFailureHandlingOptions(options);
                            try
                            {
                                if (rectGuard == null) ElementTransformUtils.MoveElement(doc, duct.Id, delta);
                                else ElementTransformUtils.MoveElements(doc, rectGuard.MoveIds, delta);
                                doc.Regenerate();
                                status = tx.Commit();
                            }
                            catch
                            {
                                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                                throw;
                            }
                        }
                    }
                    finally { app.Application.DocumentChanged -= observer; }
                    if (status != TransactionStatus.Committed || failures.Messages.Count > 0)
                        throw new InvalidOperationException("Revit tu choi phep align.\n" + string.Join("\n", failures.Messages));
                    duct = doc.GetElement(duct.Id) as Duct;
                    if (duct == null)
                        throw new InvalidOperationException("Duct khong con ton tai sau khi dich. Tool se rollback.");
                    string currentRefs = Connections(duct);
                    if (rectGuard != null) rectGuard.Validate(doc);
                    if (rectGuard != null ? rectGuard.HasPhysicalChanges(doc, added, deleted) :
                        currentRefs != originalRefs || added.Count > 0 || deleted.Count > 0)
                    {
                        List<string> reasons = new List<string>();
                        reasons.Add("Duct ID: " + duct.Id.IntegerValue);
                        if (currentRefs != originalRefs)
                        {
                            string[] before = originalRefs.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                            string[] after = currentRefs.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                            reasons.Add("Tham chieu connector bi mat: " + string.Join(", ", before.Except(after)));
                            reasons.Add("Tham chieu connector moi: " + string.Join(", ", after.Except(before)));
                            reasons.Add("Dinh dang: connector duct : ID phan tu noi : connector phan tu noi.");
                        }
                        if (added.Count > 0) reasons.Add("Revit them ID: " + string.Join(", ", added.OrderBy(id => id)));
                        if (deleted.Count > 0) reasons.Add("Revit xoa ID: " + string.Join(", ", deleted.OrderBy(id => id)));
                        throw new InvalidOperationException("Kiem tra ket noi/phan tu khong dat. Tool se rollback.\n\n" + string.Join("\n", reasons));
                    }
                    Line actual = ((LocationCurve)duct.Location).Curve as Line;
                    if (actual == null || !target.ActualMatches(frame, actual, delta, xyOnly))
                        throw new InvalidOperationException("Vi tri/chieu dai sau dich khong dung du kien. Tool se rollback.");
                    if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Khong the ket thuc transaction group.");
                    kept = true;
                    return Result.Succeeded;
                }
                finally
                {
                    if (!kept && group.GetStatus() == TransactionStatus.Started && group.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Revit chua xac nhan rollback. Kiem tra transaction truoc khi tiep tuc.");
                    ui.RefreshActiveView();
                }
            }
        }
        private static string Connections(Duct duct)
        {
            List<string> refs = new List<string>();
            foreach (Connector c in duct.ConnectorManager.Connectors)
                foreach (Connector r in c.AllRefs)
                    if (r.Owner.Id != duct.Id) refs.Add(c.Id + ":" + r.Owner.Id.IntegerValue + ":" + r.Id);
            refs.Sort(StringComparer.Ordinal);
            return string.Join("|", refs);
        }
        private sealed class DuctFilter : ISelectionFilter
        {
            private readonly bool round;
            public DuctFilter(bool round) { this.round = round; }
            public bool AllowElement(Element e)
            {
                Duct d = e as Duct;
                if (d == null) return false;
                try { return DuctFrame.Create(d).Round == round; } catch { return false; }
            }
            public bool AllowReference(Reference r, XYZ p) { return false; }
        }
        private sealed class CadFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) { return e is ImportInstance; }
            public bool AllowReference(Reference r, XYZ p) { return true; }
        }
    }
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AlignRectDuctCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        { return AlignDuctCommand.ExecuteForShape(data, ref message, false); }
    }

    internal sealed class FailureCapture : IFailuresPreprocessor
    {
        public readonly List<string> Messages = new List<string>();
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
                Messages.Add(f.GetSeverity() + ": " + f.GetDescriptionText() + " [ID " +
                    string.Join(", ", f.GetFailingElementIds().Select(i => i.IntegerValue.ToString())) + "]");
            return Messages.Count == 0 ? FailureProcessingResult.Continue : FailureProcessingResult.ProceedWithRollBack;
        }
    }
}
