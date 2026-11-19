using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;

namespace MEP_Align_Round_Duct_3D_ver1
{
    internal static class Units
    {
        public static double Ft(double mm) { return mm / 304.8; }
        public static double Mm(double feet) { return feet * 304.8; }
        public static bool Parallel(XYZ a, XYZ b) { return a.Normalize().CrossProduct(b.Normalize()).GetLength() < 0.00001; }
    }

    internal sealed class DuctFrame
    {
        public XYZ A, B, Axis, WidthAxis, HeightAxis;
        public double Width, Height;
        public bool Round;
        public XYZ Mid { get { return (A + B) * 0.5; } }
        public string Size { get { return Round ? string.Format("Round D{0:0.##}", Units.Mm(Width)) :
            string.Format("Rec {0:0.##} x {1:0.##}", Units.Mm(Width), Units.Mm(Height)); } }
        public static DuctFrame Create(Duct duct)
        {
            Line line = (duct == null ? null : duct.Location as LocationCurve)?.Curve as Line;
            if (line == null || line.Length < Units.Ft(1)) throw new InvalidOperationException("Chi ho tro Duct thang, khong ho tro Flex Duct/co/te.");
            Connector end = duct.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(c => c.ConnectorType == ConnectorType.End);
            if (end == null || (end.Shape != ConnectorProfileType.Round && end.Shape != ConnectorProfileType.Rectangular))
                throw new InvalidOperationException("Chi ho tro tiet dien round va rectangular.");
            DuctFrame f = new DuctFrame { A = line.GetEndPoint(0), B = line.GetEndPoint(1), Axis = line.Direction,
                Round = end.Shape == ConnectorProfileType.Round };
            f.WidthAxis = end.CoordinateSystem.BasisX.Normalize();
            f.HeightAxis = end.CoordinateSystem.BasisY.Normalize();
            f.Width = f.Round ? duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM).AsDouble() : duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM).AsDouble();
            f.Height = f.Round ? f.Width : duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM).AsDouble();
            return f;
        }
    }

    internal sealed class AxisCandidate
    {
        public XYZ Origin, Axis;
        public double Min, Max, ClickDistance;
        public bool SectionOnly;
        public string Source, Layer;
        public string Description { get { return Source + (string.IsNullOrEmpty(Layer) ? "" : " | Layer " + Layer) +
            (SectionOnly ? " | truc suy tu tiet dien dau ong" : ""); } }
        public XYZ Translation(DuctFrame f, bool xyOnly)
        {
            XYZ direction = Axis, p = Origin, m = f.Mid;
            if (xyOnly)
            {
                direction = new XYZ(Axis.X, Axis.Y, 0);
                if (direction.GetLength() < 1e-8) throw new InvalidOperationException("Ong dung: dung che do XYZ, khong dung XY.");
                direction = direction.Normalize(); p = new XYZ(p.X, p.Y, 0); m = new XYZ(m.X, m.Y, 0);
            }
            XYZ v = p - m;
            return v - direction * v.DotProduct(direction);
        }
        public double Residual(DuctFrame f, XYZ delta, bool xyOnly)
        { return Math.Max(Distance(f.A + delta, xyOnly), Distance(f.B + delta, xyOnly)); }
        private double Distance(XYZ point, bool xyOnly)
        {
            XYZ v = point - Origin, axis = Axis;
            if (xyOnly) { v = new XYZ(v.X, v.Y, 0); axis = new XYZ(axis.X, axis.Y, 0).Normalize(); }
            return v.CrossProduct(axis).GetLength();
        }
        public bool ActualMatches(DuctFrame f, Line actual, XYZ delta, bool xyOnly)
        {
            double tol = Units.Ft(0.1);
            XYZ a = actual.GetEndPoint(0), b = actual.GetEndPoint(1);
            bool direct = a.DistanceTo(f.A + delta) <= tol && b.DistanceTo(f.B + delta) <= tol;
            bool reverse = b.DistanceTo(f.A + delta) <= tol && a.DistanceTo(f.B + delta) <= tol;
            return (direct || reverse) && Math.Abs(actual.Length - f.A.DistanceTo(f.B)) <= tol &&
                Math.Max(Distance(a, xyOnly), Distance(b, xyOnly)) <= Units.Ft(1);
        }
    }

    internal sealed partial class CadReader
    {
        private readonly Document doc;
        private readonly DuctFrame duct;
        private readonly XYZ hit;
        private readonly List<AxisCandidate> found = new List<AxisCandidate>();
        private int primitives, solids, meshes, polylines, errors;
        private Reference picked;
        private string pickedStable;
        private bool exactFound;
        private const int Limit = 2000000;
        private readonly List<string> rejectionDetails = new List<string>();
        private void Note(string reason)
        { if (rejectionDetails.Count < 8 && !rejectionDetails.Contains(reason)) rejectionDetails.Add(reason); }
        public string Diagnostics { get { return string.Format("Da doc {0} solid, {1} mesh, {2} polyline. Bo qua {3} hinh hoc khong doc duoc.", solids, meshes, polylines, errors) +
            (duct == null ? "" : "\nDuct: " + duct.Size) + "\n" + string.Join("\n", rejectionDetails); } }
        public CadReader(Document document, DuctFrame frame, XYZ point) { doc = document; duct = frame; hit = point; }

        public List<AxisCandidate> Read(ImportInstance cad)
        { return Read(cad, null); }

        public List<AxisCandidate> Read(ImportInstance cad, Reference reference)
        {
            if (cad == null) throw new InvalidOperationException("Doi tuong da chon khong phai CAD link/import trong model nay.");
            picked = reference;
            pickedStable = reference == null ? null : reference.ConvertToStableRepresentation(doc);
            Options options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false, ComputeReferences = true };
            using (GeometryElement geometry = cad.get_Geometry(options))
            {
                if (duct.Round) Walk(geometry, Transform.Identity, "");
                else ReadPickedCenterLines(geometry, Transform.Identity, "");
            }
            if (primitives >= Limit && !exactFound) throw new InvalidOperationException("Khong xac dinh duoc khoi da pick trong gioi han doc. Khong tu chon khoi khac.");
            return found.OrderBy(c => c.ClickDistance).Take(20).ToList();
        }
        private void ReadPickedCenterLines(IEnumerable<GeometryObject> geometry, Transform transform, string inheritedLayer)
        {
            if (geometry == null) return;
            foreach (var obj in geometry)
            {
                if (exactFound) return;
                if (++primitives >= Limit) throw new InvalidOperationException("Vuot gioi han doc net CAD.");
                var instance = obj as GeometryInstance;
                if (instance != null)
                {
                    using (var symbol = instance.GetSymbolGeometry())
                    {
                        var child = transform.Multiply(instance.Transform);
                        if (ContainsHit(symbol.GetBoundingBox(), child))
                            ReadPickedCenterLines(symbol, child, Layer(obj, inheritedLayer));
                    }
                    continue;
                }
                var line = obj as Line;
                var poly = obj as PolyLine;
                if (line == null && poly == null) continue;
                if (line != null && !line.IsBound) continue;
                var points = line != null
                    ? new List<XYZ> { transform.OfPoint(line.GetEndPoint(0)), transform.OfPoint(line.GetEndPoint(1)) }
                    : poly.GetCoordinates().Select(transform.OfPoint).ToList();
                bool exact = line != null && Matches(line.Reference);
                if (!exact && !OnPolyline(points)) continue;
                if (exact) found.Clear();
                ReadCenterLine(points, Layer(obj, inheritedLayer));
                if (exact) exactFound = true;
            }
        }
        private void Walk(GeometryElement geometry, Transform transform, string inheritedLayer)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                if (exactFound) return;
                if (++primitives >= Limit) return;
                string layer = Layer(obj, inheritedLayer);
                GeometryInstance instance = obj as GeometryInstance;
                if (instance != null)
                {
                    // Symbol geometry is local: explicitly compose every nested block transform once.
                    using (GeometryElement symbol = instance.GetSymbolGeometry())
                    {
                        Transform child = transform.Multiply(instance.Transform);
                        if (ContainsHit(symbol.GetBoundingBox(), child)) Walk(symbol, child, layer);
                    }
                    continue;
                }
                try
                {
                    // Only the rectangular command accepts an explicitly picked straight CAD line.
                    // Never infer a centreline from a nearby line or from a solid's edge.
                    Line line = obj as Line;
                    if (!duct.Round && line != null && line.IsBound)
                    {
                        List<XYZ> points = new List<XYZ> { transform.OfPoint(line.GetEndPoint(0)), transform.OfPoint(line.GetEndPoint(1)) };
                        bool exact = Matches(line.Reference);
                        if (!exact && !OnPolyline(points)) continue;
                        if (exact) found.Clear();
                        ReadCenterLine(points, layer);
                        if (exact) exactFound = true;
                        continue;
                    }
                    Solid solid = obj as Solid;
                    if (solid != null)
                    {
                        bool exact = MatchesSolid(solid);
                        if (!exact && !OnSolid(solid, transform)) continue;
                        if (exact) found.Clear();
                        solids++; ReadSolid(solid, transform, layer);
                        if (exact) exactFound = true;
                        continue;
                    }
                    Mesh mesh = obj as Mesh;
                    if (mesh != null)
                    {
                        // Revit 2019 Mesh has no Reference property; use the actual hit triangle.
                        bool exact = false;
                        if (!exact && !OnMesh(mesh, transform)) continue;
                        if (exact) found.Clear();
                        meshes++; if (!duct.Round) Box(mesh.Vertices.Select(transform.OfPoint).ToList(), layer, "Khoi mesh da pick");
                        if (exact) exactFound = true;
                        continue;
                    }
                    PolyLine poly = obj as PolyLine;
                    if (poly != null)
                    {
                        List<XYZ> points = poly.GetCoordinates().Select(transform.OfPoint).ToList();
                        // Revit 2019 PolyLine has no Reference property; use point-to-segment distance.
                        bool exact = false;
                        if (!exact && !OnPolyline(points)) continue;
                        if (exact) found.Clear();
                        polylines++;
                        if (!duct.Round)
                        {
                            ReadPolyline(points, layer);
                            ReadCenterLine(points, layer);
                        }
                        if (exact) exactFound = true;
                    }
                }
                catch (Exception) { errors++; }
            }
        }
        private void ReadCenterLine(List<XYZ> points, string layer)
        {
            if (points.Count < 2) return;
            XYZ origin = points[0], vector = points[points.Count - 1] - origin;
            if (vector.GetLength() < Units.Ft(1)) return;
            XYZ axis = vector.Normalize();
            if (!Units.Parallel(axis, duct.Axis)) return;
            // Reject bends and closed outlines. A straight polyline may contain multiple segments.
            if (points.Any(p => (p - origin).CrossProduct(axis).GetLength() > Units.Ft(0.1))) return;
            double min = points.Min(p => (p - origin).DotProduct(axis));
            double max = points.Max(p => (p - origin).DotProduct(axis));
            Add(new AxisCandidate { Origin = origin, Axis = axis, Min = min, Max = max,
                Layer = layer, Source = "Net center line CAD do nguoi dung pick" });
        }
        private bool Matches(Reference reference)
        {
            if (picked == null || reference == null) return false;
            try { return reference.ConvertToStableRepresentation(doc) == pickedStable; }
            catch { return false; }
        }
        private bool MatchesSolid(Solid solid)
        {
            foreach (Face face in solid.Faces) if (Matches(face.Reference)) return true;
            foreach (Edge edge in solid.Edges) if (Matches(edge.Reference)) return true;
            return false;
        }
        private bool ContainsHit(BoundingBoxXYZ box, Transform transform)
        {
            if (box == null) return true;
            XYZ p = box.Transform.Inverse.OfPoint(transform.Inverse.OfPoint(hit));
            double e = Units.Ft(2);
            return p.X >= box.Min.X - e && p.X <= box.Max.X + e &&
                p.Y >= box.Min.Y - e && p.Y <= box.Max.Y + e && p.Z >= box.Min.Z - e && p.Z <= box.Max.Z + e;
        }
        private bool OnSolid(Solid solid, Transform transform)
        {
            if (solid.Faces.Size == 0 || !ContainsHit(solid.GetBoundingBox(), transform)) return false;
            XYZ local = transform.Inverse.OfPoint(hit);
            foreach (Face face in solid.Faces)
            {
                try
                {
                    IntersectionResult result = face.Project(local);
                    if (result != null && face.IsInside(result.UVPoint) &&
                        transform.OfPoint(result.XYZPoint).DistanceTo(hit) <= Units.Ft(2)) return true;
                }
                catch { }
            }
            return false;
        }
        private bool OnPolyline(List<XYZ> points)
        {
            for (int i = 1; i < points.Count; i++)
            {
                XYZ v = points[i] - points[i - 1];
                double length2 = v.DotProduct(v);
                if (length2 < 1e-16) continue;
                double t = Math.Max(0, Math.Min(1, (hit - points[i - 1]).DotProduct(v) / length2));
                if (hit.DistanceTo(points[i - 1] + v * t) <= Units.Ft(2)) return true;
            }
            return false;
        }
        private bool OnMesh(Mesh mesh, Transform transform)
        {
            double tolerance = Units.Ft(2);
            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle triangle = mesh.get_Triangle(i);
                XYZ a = transform.OfPoint(triangle.get_Vertex(0)), b = transform.OfPoint(triangle.get_Vertex(1)), c = transform.OfPoint(triangle.get_Vertex(2));
                XYZ ab = b - a, ac = c - a, n = ab.CrossProduct(ac);
                if (n.GetLength() < 1e-12) continue;
                n = n.Normalize();
                double height = (hit - a).DotProduct(n);
                if (Math.Abs(height) > tolerance) continue;
                XYZ v = hit - n * height - a;
                double d00 = ab.DotProduct(ab), d01 = ab.DotProduct(ac), d11 = ac.DotProduct(ac);
                double d20 = v.DotProduct(ab), d21 = v.DotProduct(ac), denominator = d00 * d11 - d01 * d01;
                if (Math.Abs(denominator) < 1e-20) continue;
                double u = (d11 * d20 - d01 * d21) / denominator, w = (d00 * d21 - d01 * d20) / denominator;
                if (u >= -1e-6 && w >= -1e-6 && u + w <= 1 + 1e-6) return true;
            }
            return false;
        }
        private string Layer(GeometryObject obj, string fallback)
        {
            if (obj.GraphicsStyleId == ElementId.InvalidElementId) return fallback;
            GraphicsStyle style = doc.GetElement(obj.GraphicsStyleId) as GraphicsStyle;
            return style?.GraphicsStyleCategory?.Name ?? fallback;
        }
        private void ReadSolid(Solid solid, Transform tr, string layer)
        {
            if (solid.Faces.Size == 0) { Note("Solid rong, khong co mat."); return; }
            List<CylindricalFace> cylinders = new List<CylindricalFace>();
            foreach (Face face in solid.Faces)
            {
                if (face is CylindricalFace) cylinders.Add((CylindricalFace)face);
                else if (!(face is PlanarFace)) { Note("Solid co mat cong: " + face.GetType().Name + " | " + layer); return; }
            }
            if (duct.Round)
            {
                if (cylinders.Count == 0) return;
                CylindricalFace first = cylinders[0];
                XYZ axis = tr.OfVector(first.Axis).Normalize(), origin = tr.OfPoint(first.Origin);
                if (!Units.Parallel(axis, duct.Axis)) return;
                double radius = tr.OfVector(first.get_Radius(0)).GetLength();
                if (Math.Abs(2 * radius - duct.Width) > Units.Ft(2)) return;
                List<XYZ> vertices = new List<XYZ>();
                foreach (CylindricalFace face in cylinders)
                {
                    if (!Units.Parallel(axis, tr.OfVector(face.Axis)) ||
                        (tr.OfPoint(face.Origin) - origin).CrossProduct(axis).GetLength() > Units.Ft(0.1) ||
                        Math.Abs(tr.OfVector(face.get_Radius(0)).GetLength() - radius) > Units.Ft(0.1) ||
                        Math.Abs(tr.OfVector(face.get_Radius(1)).GetLength() - radius) > Units.Ft(0.1)) return;
                    vertices.AddRange(face.Triangulate().Vertices.Select(tr.OfPoint));
                }
                if (vertices.Count < 4) return;
                double min = vertices.Min(p => (p - origin).DotProduct(axis)), max = vertices.Max(p => (p - origin).DotProduct(axis));
                if (max - min < Units.Ft(20)) return;
                Add(new AxisCandidate { Origin = origin, Axis = axis, Min = min, Max = max, Source = "CAD mat tru D" + Units.Mm(2 * radius).ToString("0.##"), Layer = layer });
            }
            else
            {
                if (cylinders.Count > 0) { Note("Solid co mat tru, khong phai hop chu nhat | " + layer); return; }
                List<XYZ> vertices = new List<XYZ>();
                foreach (Edge edge in solid.Edges)
                {
                    Curve c = edge.AsCurve(); if (!(c is Line)) { Note("Canh solid khong thang: " + c.GetType().Name + " | " + layer); return; }
                    vertices.Add(tr.OfPoint(c.GetEndPoint(0))); vertices.Add(tr.OfPoint(c.GetEndPoint(1)));
                }
                Box(vertices, layer, "CAD solid hop");
            }
        }
        private void Box(List<XYZ> points, string layer, string source)
        {
            if (points.Count < 8) { Note("Khong du dinh de nhan khoi hop | " + layer); return; }
            XYZ anchor = points[0];
            XYZ[] basis = { duct.Axis, duct.WidthAxis, duct.HeightAxis };
            double[][] values = points.Select(p => basis.Select(v => (p - anchor).DotProduct(v)).ToArray()).ToArray();
            double[] lo = Enumerable.Range(0, 3).Select(i => values.Min(v => v[i])).ToArray();
            double[] hi = Enumerable.Range(0, 3).Select(i => values.Max(v => v[i])).ToArray();
            double tol = Units.Ft(0.1);
            Note(string.Format("CAD chieu theo truc duct: L={0:0.###}, W={1:0.###}, H={2:0.###} mm | {3}",
                Units.Mm(hi[0] - lo[0]), Units.Mm(hi[1] - lo[1]), Units.Mm(hi[2] - lo[2]), layer));
            if (hi[0] - lo[0] < Units.Ft(20) || hi[1] - lo[1] < tol || hi[2] - lo[2] < tol)
            { Note("Khoi CAD qua ngan hoac tiet dien suy bien."); return; }
            // Require all eight actual box corners; do not accept the bounding box of a reducer/tee.
            HashSet<int> corners = new HashSet<int>();
            foreach (double[] v in values)
            {
                int key = 0;
                for (int i = 0; i < 3; i++)
                {
                    if (Math.Abs(v[i] - lo[i]) <= tol) { }
                    else if (Math.Abs(v[i] - hi[i]) <= tol) key |= 1 << i;
                    else { Note("Dinh CAD khong nam tren goc hop theo truc duct: khoi khong hop hoac khac huong."); return; }
                }
                corners.Add(key);
            }
            if (corners.Count != 8) { Note("Khong du 8 goc hop CAD."); return; }
            XYZ origin = anchor + duct.WidthAxis * ((lo[1] + hi[1]) / 2) + duct.HeightAxis * ((lo[2] + hi[2]) / 2);
            Add(new AxisCandidate { Origin = origin, Axis = duct.Axis, Min = lo[0], Max = hi[0], Layer = layer, Source = source });
        }
        private void ReadPolyline(List<XYZ> points, string layer)
        {
            // A closed four-corner rectangle perpendicular to the duct gives an end-section centre.
            double tol = Units.Ft(0.1);
            if (points.Count != 5 || points[0].DistanceTo(points[4]) > tol) return;
            List<XYZ> corners = points.Take(4).ToList();
            XYZ a = corners[1] - corners[0], b = corners[2] - corners[1];
            if (a.GetLength() < tol || b.GetLength() < tol) return;
            XYZ normal = a.CrossProduct(b);
            if (normal.GetLength() < 1e-10 || !Units.Parallel(normal, duct.Axis)) return;
            if (!Units.Parallel(a, duct.WidthAxis) && !Units.Parallel(a, duct.HeightAxis)) return;
            if (!Units.Parallel(b, duct.WidthAxis) && !Units.Parallel(b, duct.HeightAxis)) return;
            if (Math.Abs(a.Normalize().DotProduct(b.Normalize())) > 0.00001) return;
            XYZ centre = corners.Aggregate(XYZ.Zero, (sum, p) => sum + p) / 4;
            double w = corners.Max(p => (p - centre).DotProduct(duct.WidthAxis)) - corners.Min(p => (p - centre).DotProduct(duct.WidthAxis));
            double h = corners.Max(p => (p - centre).DotProduct(duct.HeightAxis)) - corners.Min(p => (p - centre).DotProduct(duct.HeightAxis));
            if (w < tol || h < tol) return;
            // Check all four corners, including the otherwise unchecked closing sides.
            if (corners.Any(p => Math.Abs(Math.Abs((p - centre).DotProduct(duct.WidthAxis)) - w / 2) > tol ||
                Math.Abs(Math.Abs((p - centre).DotProduct(duct.HeightAxis)) - h / 2) > tol ||
                Math.Abs((p - centre).DotProduct(duct.Axis)) > tol)) return;
            Add(new AxisCandidate { Origin = centre, Axis = duct.Axis, Min = 0, Max = 0, SectionOnly = true,
                Layer = layer, Source = "CAD tiet dien chu nhat" });
        }
        private void Add(AxisCandidate candidate)
        {
            XYZ v = hit - candidate.Origin;
            double t = v.DotProduct(candidate.Axis);
            XYZ nearest = candidate.Origin + candidate.Axis * Math.Max(candidate.Min, Math.Min(candidate.Max, t));
            candidate.ClickDistance = hit.DistanceTo(nearest);
            // The owning geometry has already been checked against the actual picked reference/point.
            // The user chose this body explicitly. Do not reject its centreline just because
            // the current Revit duct lies outside the CAD body's longitudinal interval.
            // Merge only the same spatial axis. Candidate list remains explicit for different nearby ducts.
            AxisCandidate existing = found.FirstOrDefault(c => Units.Parallel(c.Axis, candidate.Axis) &&
                (candidate.Origin - c.Origin).CrossProduct(c.Axis).GetLength() <= Units.Ft(0.1));
            if (existing != null)
            {
                if (candidate.ClickDistance < existing.ClickDistance)
                { found.Remove(existing); found.Add(candidate); }
                return;
            }
            found.Add(candidate);
        }
    }
}
