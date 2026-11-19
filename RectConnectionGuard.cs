using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace MEP_Align_Round_Duct_3D_ver1
{
    // Snapshot physical ports across the connected component, including the far side of fittings.
    internal sealed class RectConnectionGuard
    {
        private readonly Dictionary<string, double> originalGaps = new Dictionary<string, double>();
        private readonly Dictionary<int, string> topology = new Dictionary<int, string>();
        internal readonly List<ElementId> MoveIds = new List<ElementId>();
        internal readonly HashSet<int> OriginalMetadataIds;
        private readonly bool includePiping;

        private RectConnectionGuard(Document doc, bool piping = false)
        {
            includePiping = piping;
            OriginalMetadataIds = new HashSet<int>(new FilteredElementCollector(doc).OfClass(typeof(MEPSystem))
                .ToElementIds().Select(id => id.IntegerValue));
            OriginalMetadataIds.UnionWith(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds().Select(id => id.IntegerValue));
        }
        private IEnumerable<Connector> Ports(Element element)
        {
            ConnectorManager manager = (element as MEPCurve)?.ConnectorManager
                ?? (element as FamilyInstance)?.MEPModel?.ConnectorManager;
            if (manager == null) return Enumerable.Empty<Connector>();
            return manager.Connectors.Cast<Connector>().Where(c => SupportedDomain(c.Domain) &&
                c.ConnectorType != ConnectorType.Logical);
        }
        private bool SupportedDomain(Domain domain)
        { return domain == Domain.DomainHvac || (includePiping && domain == Domain.DomainPiping); }
        private IEnumerable<Connector> Neighbours(Connector port)
        {
            return port.AllRefs.Cast<Connector>().Where(r => r.Owner.Id != port.Owner.Id &&
                SupportedDomain(r.Domain) && r.ConnectorType != ConnectorType.Logical && port.IsConnectedTo(r));
        }
        private string Signature(Element element, bool checkGaps)
        {
            List<string> rows = new List<string>();
            foreach (Connector port in Ports(element))
            {
                List<string> peers = new List<string>();
                foreach (Connector peer in Neighbours(port))
                {
                    string key = element.Id.IntegerValue + ":" + port.Id + ":" + peer.Owner.Id.IntegerValue + ":" + peer.Id;
                    double gap = port.Origin.DistanceTo(peer.Origin);
                    if (!checkGaps) originalGaps[key] = gap;
                    double baseline;
                    if (checkGaps && port.ConnectorType == ConnectorType.End && peer.ConnectorType == ConnectorType.End &&
                        (!originalGaps.TryGetValue(key, out baseline) || gap > Math.Max(Units.Ft(0.1), baseline + Units.Ft(0.01))))
                        throw new InvalidOperationException("Ho ket noi giua ID " + element.Id.IntegerValue + " va " + peer.Owner.Id.IntegerValue);
                    peers.Add(peer.Owner.Id.IntegerValue + ":" + peer.Id);
                }
                peers.Sort(StringComparer.Ordinal);
                // Include unused ports so an unexpected new connection is detected too.
                rows.Add(port.Id + "=" + string.Join(",", peers));
            }
            rows.Sort(StringComparer.Ordinal);
            return string.Join("|", rows);
        }
        internal static RectConnectionGuard Capture(Document doc, Duct duct)
        { return CaptureCore(doc, duct, false); }
        internal static RectConnectionGuard CaptureFamily(Document doc, FamilyInstance family)
        { return CaptureCore(doc, family, true); }
        private static RectConnectionGuard CaptureCore(Document doc, Element duct, bool piping)
        {
            RectConnectionGuard guard = new RectConnectionGuard(doc, piping);
            Queue<Element> pending = new Queue<Element>();
            pending.Enqueue(duct);
            while (pending.Count > 0)
            {
                Element element = pending.Dequeue();
                int id = element.Id.IntegerValue;
                if (guard.topology.ContainsKey(id)) continue;
                if (guard.topology.Count >= 10000) throw new InvalidOperationException("Mang noi vuot 10000 phan tu; khong the kiem tra an toan.");
                guard.topology.Add(id, guard.Signature(element, false));
                foreach (Connector port in guard.Ports(element))
                    foreach (Connector peer in guard.Neighbours(port))
                        if (!guard.topology.ContainsKey(peer.Owner.Id.IntegerValue)) pending.Enqueue(peer.Owner);
            }
            // Translate the selected duct and its contiguous fitting cluster only.
            // Other ducts remain under Revit's connected-network solver.
            HashSet<int> moving = new HashSet<int>();
            pending.Enqueue(duct);
            while (pending.Count > 0)
            {
                Element element = pending.Dequeue();
                if (!moving.Add(element.Id.IntegerValue)) continue;
                if (element.Pinned || element.GroupId != ElementId.InvalidElementId)
                    throw new InvalidOperationException("Duct/fitting ID " + element.Id.IntegerValue + " dang Pin hoac thuoc Group.");
                guard.MoveIds.Add(element.Id);
                foreach (Connector port in guard.Ports(element))
                    foreach (Connector peer in guard.Neighbours(port))
                        if (peer.Owner.Category != null && (peer.Owner.Category.Id.IntegerValue == (int)BuiltInCategory.OST_DuctFitting ||
                            (piping && peer.Owner.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting)) &&
                            !moving.Contains(peer.Owner.Id.IntegerValue)) pending.Enqueue(peer.Owner);
            }
            return guard;
        }
        internal void Validate(Document doc)
        {
            foreach (var item in topology)
            {
                Element element = doc.GetElement(new ElementId(item.Key));
                if (element == null || Signature(element, true) != item.Value)
                    throw new InvalidOperationException("Ket noi vat ly thay doi tai ID " + item.Key + ". Tool se rollback.");
            }
        }
        internal void Prepare(UIApplication app, Document doc, XYZ delta)
        {
            // Generated FamilySymbols are not returned by FilteredElementCollector.
            // Trial the same move, roll it back, then classify deleted IDs in the restored document.
            HashSet<int> deleted = new HashSet<int>();
            EventHandler<DocumentChangedEventArgs> observer = (sender, args) =>
            {
                if (args.GetDocument().Equals(doc))
                    foreach (ElementId id in args.GetDeletedElementIds()) deleted.Add(id.IntegerValue);
            };
            using (TransactionGroup trial = new TransactionGroup(doc, "Check connected duct move"))
            {
                trial.Start();
                try
                {
                    app.Application.DocumentChanged += observer;
                    using (Transaction tx = new Transaction(doc, "Check fittings"))
                    {
                        tx.Start();
                        FailureCapture failures = new FailureCapture();
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetClearAfterRollback(true).SetFailuresPreprocessor(failures));
                        ElementTransformUtils.MoveElements(doc, MoveIds, delta);
                        doc.Regenerate();
                        if (tx.Commit() != TransactionStatus.Committed || failures.Messages.Count > 0)
                            throw new InvalidOperationException("Revit khong the giu ket noi khi dich:\n" + string.Join("\n", failures.Messages));
                        Validate(doc);
                    }
                }
                finally
                {
                    app.Application.DocumentChanged -= observer;
                    if (trial.GetStatus() == TransactionStatus.Started && trial.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Khong the rollback buoc kiem tra ket noi.");
                }
            }
            foreach (int id in deleted)
            {
                Element restored = doc.GetElement(new ElementId(id));
                if (restored is ElementType || restored is MEPSystem) OriginalMetadataIds.Add(id);
                else throw new InvalidOperationException("Phep dich se xoa phan tu ID " + id + ". Da huy buoc thu.");
            }
        }
        internal bool HasPhysicalChanges(Document doc, HashSet<int> added, HashSet<int> deleted)
        {
            // Revit may regenerate logical systems and unused family types during movement.
            // Physical instances, annotations and all other elements remain protected.
            return added.Any(id => !(doc.GetElement(new ElementId(id)) is MEPSystem) && !(doc.GetElement(new ElementId(id)) is ElementType)) ||
                deleted.Any(id => !OriginalMetadataIds.Contains(id));
        }
    }
}
