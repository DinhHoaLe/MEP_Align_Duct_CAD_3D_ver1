using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace MEP_Align_Round_Duct_3D_ver1
{
    internal sealed partial class CadReader
    {
        private sealed class BlockResult
        {
            public bool Hit, Exact;
            public int Depth;
            public List<AxisCandidate> Axes = new List<AxisCandidate>();
        }

        private void ReadRectBlocks(GeometryElement geometry)
        {
            BlockResult result = FindRectBlock(geometry, Transform.Identity, "", 0);
            found.Clear();
            foreach (AxisCandidate axis in result.Axes) Add(axis);
            // A unique owning block is not the same as a unique duct axis within that block.
            if (found.Count > 1)
            {
                List<AxisCandidate> local = found.Where(AxisContainsClick).ToList();
                if (local.Count == 1) { found.Clear(); found.Add(local[0]); }
            }
        }

        private bool AxisContainsClick(AxisCandidate axis)
        {
            XYZ v = hit - axis.Origin;
            double along = v.DotProduct(axis.Axis);
            return along >= axis.Min - Units.Ft(2) && along <= axis.Max + Units.Ft(2) &&
                Math.Abs(v.DotProduct(duct.WidthAxis)) <= duct.Width / 2 + Units.Ft(2) &&
                Math.Abs(v.DotProduct(duct.HeightAxis)) <= duct.Height / 2 + Units.Ft(2);
        }

        // Find actual primitive hits first. A bounding box only prunes traversal, never proves ownership.
        private BlockResult FindRectBlock(IEnumerable<GeometryObject> geometry, Transform tr, string layer, int depth)
        {
            BlockResult result = new BlockResult { Depth = depth };
            List<BlockResult> children = new List<BlockResult>();
            bool directHit = false, directExact = false;
            List<AxisCandidate> hitBodies = new List<AxisCandidate>();
            if (geometry == null) return result;
            foreach (GeometryObject obj in geometry)
            {
                if (++primitives >= Limit) throw new InvalidOperationException("Block CAD qua phuc tap; da dung doc, khong tu chon truc.");
                string ownLayer = Layer(obj, layer);
                GeometryInstance instance = obj as GeometryInstance;
                if (instance != null)
                {
                    using (GeometryElement symbol = instance.GetSymbolGeometry())
                    {
                        Transform childTransform = tr.Multiply(instance.Transform);
                        if (ContainsHit(symbol.GetBoundingBox(), childTransform))
                        {
                            BlockResult child = FindRectBlock(symbol, childTransform, ownLayer, depth + 1);
                            if (child.Hit) children.Add(child);
                        }
                    }
                    continue;
                }
                bool exact;
                if (!RectPrimitiveHit(obj, tr, out exact)) continue;
                directHit = true; directExact |= exact;
                found.Clear();
                ReadRectBody(obj, tr, ownLayer);
                hitBodies.AddRange(found);
            }
            result.Hit = directHit || children.Count > 0;
            result.Exact = directExact || children.Any(c => c.Exact);
            if (!result.Hit) return result;
            // Exact references disambiguate coincident blocks. Without one, retain ambiguity.
            if (children.Any(c => c.Exact)) children = children.Where(c => c.Exact).ToList();
            else if (directExact) children.Clear();
            if (children.Count > 1)
                throw new InvalidOperationException("Diem click cham nhieu block CAD. Hay click lai vao than block duct can can.");
            if (children.Count == 1 && children[0].Axes.Count > 0) return children[0];
            // A directly hit rectangular body is authoritative, even inside a larger block.
            if (hitBodies.Count > 0) { result.Axes = hitBodies; return result; }

            // Depth 0/1 can represent the entire DWG, not a single nested CAD block.
            // Never expand that scope to find an unrelated centreline elsewhere in the drawing.
            List<AxisCandidate> axes = depth >= 2 ? ReadRectBlockAxes(geometry, tr, layer) : hitBodies;
            result.Axes = axes;
            return result;
        }

        private bool RectPrimitiveHit(GeometryObject obj, Transform tr, out bool exact)
        {
            exact = false;
            Solid solid = obj as Solid;
            if (solid != null) { exact = MatchesSolid(solid); return exact || OnSolid(solid, tr); }
            Curve curve = obj as Curve;
            if (curve != null)
            {
                exact = Matches(curve.Reference);
                return exact || (curve.IsBound && OnPolyline(curve.Tessellate().Select(tr.OfPoint).ToList()));
            }
            PolyLine poly = obj as PolyLine;
            if (poly != null) return OnPolyline(poly.GetCoordinates().Select(tr.OfPoint).ToList());
            Mesh mesh = obj as Mesh;
            return mesh != null && OnMesh(mesh, tr);
        }

        private void ReadRectBody(GeometryObject obj, Transform tr, string layer)
        {
            Solid solid = obj as Solid;
            if (solid != null)
            {
                solids++;
                // Revit can collapse thousands of disconnected CAD bodies into one Solid.
                // Only accept a separated volume whose actual surface contains the click.
                if (solid.Faces.Size > 6)
                {
                    var parts = SolidUtils.SplitVolumes(solid);
                    try
                    {
                        if (parts.Count > 1)
                        {
                            var touched = parts.Where(p => ContainsHit(p.GetBoundingBox(), tr) && OnSolid(p, tr)).ToList();
                            if (touched.Count > 1)
                                throw new InvalidOperationException("Diem click cham nhieu khoi trong solid gop. Hay click giua mat than khoi can can.");
                            if (touched.Count == 1) ReadSolid(touched[0], tr, layer);
                            else Note("Khong co mat khoi tach nao tai diem click.");
                            return;
                        }
                    }
                    finally { foreach (var part in parts) part.Dispose(); }
                }
                ReadSolid(solid, tr, layer); return;
            }
            Mesh mesh = obj as Mesh;
            if (mesh != null) { meshes++; Box(mesh.Vertices.Select(tr.OfPoint).ToList(), layer, "Tim khoi chu nhat trong block"); return; }
            PolyLine poly = obj as PolyLine;
            if (poly != null) { polylines++; ReadPolyline(poly.GetCoordinates().Select(tr.OfPoint).ToList(), layer); }
        }

        private List<AxisCandidate> ReadRectBlockAxes(IEnumerable<GeometryObject> geometry, Transform tr, string layer)
        {
            List<AxisCandidate> bodies = new List<AxisCandidate>(), lines = new List<AxisCandidate>();
            CollectRectBlock(geometry, tr, layer, bodies, lines);
            // Prefer centreline geometry corroborated by a rectangular body/section.
            // A free line is accepted automatically only on an explicitly named centreline layer.
            List<AxisCandidate> accepted = new List<AxisCandidate>(bodies);
            foreach (AxisCandidate line in lines)
            {
                bool confirmed = bodies.Any(body => Units.Parallel(body.Axis, line.Axis) &&
                    (line.Origin - body.Origin).CrossProduct(body.Axis).GetLength() <= Units.Ft(0.1));
                if (confirmed || (bodies.Count == 0 && IsCenterLineLayer(line.Layer))) accepted.Add(line);
            }
            found.Clear();
            foreach (AxisCandidate axis in accepted) Add(axis);
            return found.ToList();
        }

        private void CollectRectBlock(IEnumerable<GeometryObject> geometry, Transform tr, string layer,
            List<AxisCandidate> bodies, List<AxisCandidate> lines)
        {
            foreach (GeometryObject obj in geometry)
            {
                if (++primitives >= Limit) throw new InvalidOperationException("Vuot gioi han doc hinh hoc block CAD.");
                string ownLayer = Layer(obj, layer);
                GeometryInstance instance = obj as GeometryInstance;
                if (instance != null)
                {
                    using (GeometryElement symbol = instance.GetSymbolGeometry())
                        CollectRectBlock(symbol, tr.Multiply(instance.Transform), ownLayer, bodies, lines);
                    continue;
                }
                found.Clear();
                ReadRectBody(obj, tr, ownLayer);
                bodies.AddRange(found);
                found.Clear();
                Line line = obj as Line;
                PolyLine poly = obj as PolyLine;
                if (line != null && line.IsBound)
                    ReadCenterLine(new List<XYZ> { tr.OfPoint(line.GetEndPoint(0)), tr.OfPoint(line.GetEndPoint(1)) }, ownLayer);
                else if (poly != null) ReadCenterLine(poly.GetCoordinates().Select(tr.OfPoint).ToList(), ownLayer);
                foreach (AxisCandidate axis in found) axis.Source = "Center line trong block CAD";
                lines.AddRange(found);
            }
        }

        private static bool IsCenterLineLayer(string layer)
        {
            return Regex.IsMatch(layer ?? "", @"(^|[^A-Z0-9])(CENTER(LINE)?|CENTRE(LINE)?|CL|TIM)([^A-Z0-9]|$)", RegexOptions.IgnoreCase);
        }
    }
}
