# -*- coding: utf-8 -*-
"""Geometry diagnostics and bounded alignment tests which always roll back."""
from pyrevit import routes, DB
import json
import os

api = routes.API('align-diagnostics')


def xyz(p):
    return [p.X * 304.8, p.Y * 304.8, p.Z * 304.8]


@api.route('/model', methods=['GET'])
def model(doc, uidoc):
    if doc is None:
        return {'error': 'No active document'}
    ducts = []
    for d in DB.FilteredElementCollector(doc).OfClass(DB.Mechanical.Duct):
        row = {'id': d.Id.IntegerValue, 'pinned': d.Pinned}
        try:
            c = d.Location.Curve
            row['ends_mm'] = [xyz(c.GetEndPoint(0)), xyz(c.GetEndPoint(1))]
            row['connectors'] = [{'shape': str(k.Shape), 'origin_mm': xyz(k.Origin),
                                  'connected': k.IsConnected} for k in d.ConnectorManager.Connectors]
            for name, param in [('diameter', DB.BuiltInParameter.RBS_CURVE_DIAMETER_PARAM),
                                ('width', DB.BuiltInParameter.RBS_CURVE_WIDTH_PARAM),
                                ('height', DB.BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)]:
                p = d.get_Parameter(param)
                if p:
                    row[name + '_mm'] = p.AsDouble() * 304.8
        except Exception as ex:
            row['error'] = str(ex)
        ducts.append(row)
    imports = []
    for i in DB.FilteredElementCollector(doc).OfClass(DB.ImportInstance):
        imports.append({'id': i.Id.IntegerValue, 'name': i.Category.Name if i.Category else '',
                        'linked': i.IsLinked, 'view_specific': i.ViewSpecific})
    return {'title': doc.Title, 'view': doc.ActiveView.Name,
            'selected_ids': [i.IntegerValue for i in uidoc.Selection.GetElementIds()],
            'ducts': ducts, 'cad': imports, 'units': 'mm'}


@api.route('/cad/<int:element_id>', methods=['GET'])
def cad(doc, element_id):
    element = doc.GetElement(DB.ElementId(element_id))
    if not isinstance(element, DB.ImportInstance):
        return {'error': 'Expected CAD ImportInstance'}
    counts, layers, samples, cylinders = {}, {}, [], []
    segments = []
    def walk(geometry):
        for g in geometry:
            kind = g.GetType().Name
            counts[kind] = counts.get(kind, 0) + 1
            layer = ''
            try:
                gs = doc.GetElement(g.GraphicsStyleId)
                if gs:
                    layer = gs.GraphicsStyleCategory.Name
            except Exception:
                pass
            if layer:
                layers[layer] = layers.get(layer, 0) + 1
            if isinstance(g, DB.GeometryInstance):
                # Instance geometry already applies its local transform, including nested blocks.
                walk(g.GetInstanceGeometry())
            elif isinstance(g, DB.Line):
                if len(segments) < 100000:
                    segments.append({'layer': layer, 'a': xyz(g.GetEndPoint(0)), 'b': xyz(g.GetEndPoint(1))})
            elif isinstance(g, DB.PolyLine):
                pts = list(g.GetCoordinates())
                for a, b in zip(pts, pts[1:]):
                    if len(segments) < 100000:
                        segments.append({'layer': layer, 'a': xyz(a), 'b': xyz(b)})
            elif isinstance(g, DB.Solid):
                for f in g.Faces:
                    fk = f.GetType().Name
                    counts['face:' + fk] = counts.get('face:' + fk, 0) + 1
                    if isinstance(f, DB.CylindricalFace) and len(cylinders) < 10000:
                        cylinders.append({'origin': xyz(f.Origin), 'axis': [f.Axis.X, f.Axis.Y, f.Axis.Z],
                                          'radii_mm': [f.Radius[0].GetLength() * 304.8,
                                                       f.Radius[1].GetLength() * 304.8]})
                for edge in g.Edges:
                    c = edge.AsCurve()
                    if isinstance(c, DB.Line) and len(segments) < 100000:
                        segments.append({'layer': layer, 'a': xyz(c.GetEndPoint(0)), 'b': xyz(c.GetEndPoint(1))})
            elif len(samples) < 15:
                samples.append({'type': kind, 'layer': layer})
    options = DB.Options()
    options.IncludeNonVisibleObjects = False
    options.DetailLevel = DB.ViewDetailLevel.Fine
    try:
        walk(element.get_Geometry(options))
        return {'id': element_id, 'counts': counts, 'layers': layers, 'samples': samples,
                'segments': segments, 'cylinders': cylinders,
                'segments_truncated': len(segments) >= 100000, 'units': 'mm'}
    except Exception as ex:
        return {'error': str(ex), 'counts': counts, 'segments': segments,
                'layers': layers, 'cylinders': cylinders}


@api.route('/test-samples', methods=['POST'])
def test_samples(doc):
    """Fixed sample alignment, regenerated then rolled back. Never commits."""
    if doc is None or doc.Title != 'ABT_11115_430_RLT-_5_80_001_Test_Rev01_lcns_adsk_023':
        return {'error': 'Sample document does not match'}
    results = []
    for duct_id, cad_id, delta in [
            (13920299, 18095596, [0.0, 0.0, 0.0]),
            (1653278, 18099500, [0.0, 35.000000099651515, 3.5533905952115674])]:
        d = doc.GetElement(DB.ElementId(duct_id))
        if not isinstance(d, DB.Mechanical.Duct):
            return {'error': 'Sample duct missing'}
        before = [xyz(d.Location.Curve.GetEndPoint(i)) for i in (0, 1)]
        connected_before = sum(1 for c in d.ConnectorManager.Connectors if c.IsConnected)
        t = DB.Transaction(doc, 'Diagnostic alignment - always rollback')
        row = {'duct_id': duct_id, 'cad_id': cad_id, 'requested_shift_mm': delta}
        try:
            t.Start()
            if any(abs(v) > 0.000001 for v in delta):
                DB.ElementTransformUtils.MoveElement(doc, d.Id, DB.XYZ(*[v / 304.8 for v in delta]))
            doc.Regenerate()
            row['before_mm'] = before
            row['during_mm'] = [xyz(d.Location.Curve.GetEndPoint(i)) for i in (0, 1)]
            row['connected_before'] = connected_before
            row['connected_during'] = sum(1 for c in d.ConnectorManager.Connectors if c.IsConnected)
            row['regeneration_succeeded'] = True
        except Exception as ex:
            row['error'] = str(ex)
        finally:
            if t.GetStatus() == DB.TransactionStatus.Started:
                row['rollback_status'] = str(t.RollBack())
            t.Dispose()
        restored = doc.GetElement(DB.ElementId(duct_id))
        row['after_rollback_mm'] = [xyz(restored.Location.Curve.GetEndPoint(i)) for i in (0, 1)]
        row['connected_after_rollback'] = sum(1 for c in restored.ConnectorManager.Connectors if c.IsConnected)
        results.append(row)
    return {'mode': 'Regenerate then rollback; final transaction commit and network-wide effects not tested',
            'results': results}


@api.route('/scan/<int:first>/<int:count>', methods=['GET'])
def scan(doc, first, count):
    if count < 1 or count > 100:
        return {'error': 'Count must be 1..100'}
    rows = []
    for eid in range(first, first + count):
        element = doc.GetElement(DB.ElementId(eid))
        if isinstance(element, DB.ImportInstance) and not element.IsLinked:
            rows.append(cad(doc, eid))
    return {'items': rows}


@api.route('/manual-reader-smoke', methods=['GET'])
def manual_reader_smoke(doc):
    """Read-only check of the compiled C# geometry reader on the fixed 20 samples."""
    from System import Array, Object, Activator
    from System.Reflection import Assembly
    root = r'F:\Hoa Le\1. Working\Tools\MEP_Align_Round_Duct_3D_ver1\MEP_Align_Round_Duct_3D_ver1'
    with open(os.path.join(root, 'batch-test-pairs.json')) as source:
        pairs = json.load(source)['pairs']
    assembly = Assembly.LoadFrom(os.path.join(root, 'bin', 'ReaderSmoke', 'MEP_Align_Round_Duct_3D_ver1.dll'))
    prefix = 'MEP_Align_Round_Duct_3D_ver1.'
    frame_type = assembly.GetType(prefix + 'DuctFrame')
    reader_type = assembly.GetType(prefix + 'CadReader')
    candidate_type = assembly.GetType(prefix + 'AxisCandidate')
    rows = []
    for pair in pairs:
        row = {'duct_id': pair['duct_id'], 'cad_id': pair['cad_id'], 'shape': pair['shape']}
        try:
            duct = doc.GetElement(DB.ElementId(pair['duct_id']))
            target = doc.GetElement(DB.ElementId(pair['cad_id']))
            frame = frame_type.GetMethod('Create').Invoke(None, Array[Object]([duct]))
            # A point on the actual target surface, deliberately away from its centreline.
            offset = DB.XYZ(0, 0, pair['size_mm'][-1] / 2.0 / 304.8)
            origin = DB.XYZ(*[v / 304.8 for v in pair['target_origin_mm']])
            reader = Activator.CreateInstance(reader_type, Array[Object]([doc, frame, origin + offset]))
            candidates = reader_type.GetMethod('Read').Invoke(reader, Array[Object]([target]))
            row['candidate_count'] = candidates.Count
            errors = []
            for candidate in candidates:
                move = candidate_type.GetMethod('Translation').Invoke(candidate, Array[Object]([frame, False]))
                residual = candidate_type.GetMethod('Residual').Invoke(candidate, Array[Object]([frame, move, False]))
                expected = DB.XYZ(*[v / 304.8 for v in pair['shift_mm']])
                errors.append({'shift_difference_mm': move.DistanceTo(expected) * 304.8,
                               'residual_mm': float(residual) * 304.8})
            row['candidates'] = errors
            row['pass'] = any(x['shift_difference_mm'] < 0.1 and x['residual_mm'] < 1 for x in errors)
        except Exception as ex:
            row['error'] = str(ex)
            row['pass'] = False
        rows.append(row)
    return {'mode': 'Compiled C# reader only; no transaction or model movement', 'results': rows}


def connections(d):
    return sorted([sorted([r.Owner.Id.IntegerValue for r in c.AllRefs
                           if r.Owner.Id != d.Id]) for c in d.ConnectorManager.Connectors])


class CaptureFailures(DB.IFailuresPreprocessor):
    def __init__(self):
        self.messages = []

    def PreprocessFailures(self, accessor):
        errors = False
        for failure in accessor.GetFailureMessages():
            severity = failure.GetSeverity()
            self.messages.append({'severity': str(severity), 'description': failure.GetDescriptionText(),
                                  'ids': [i.IntegerValue for i in failure.GetFailingElementIds()]})
            if severity == DB.FailureSeverity.Warning:
                accessor.DeleteWarning(failure)
            else:
                errors = True
        return DB.FailureProcessingResult.ProceedWithRollBack if errors else DB.FailureProcessingResult.Continue


@api.route('/test-batch', methods=['POST'])
def test_batch(doc, request):
    """At most 20 explicitly specified tests. No commit or arbitrary code route."""
    if doc is None or doc.Title != 'ABT_11115_430_RLT-_5_80_001_Test_Rev01_lcns_adsk_023':
        return {'error': 'Sample document does not match'}
    pairs = request.data.get('pairs', [])
    if not pairs or len(pairs) > 20 or len(set(p['duct_id'] for p in pairs)) != len(pairs):
        return {'error': 'Require 1..20 distinct duct ids'}
    results = []
    for pair in pairs:
        row = dict(pair)
        d = doc.GetElement(DB.ElementId(int(pair['duct_id'])))
        target = doc.GetElement(DB.ElementId(int(pair['cad_id'])))
        if not isinstance(d, DB.Mechanical.Duct) or not isinstance(target, DB.ImportInstance):
            return {'error': 'Duct/CAD missing', 'results': results}
        delta = pair['shift_mm']
        if len(delta) != 3 or max(abs(float(v)) for v in delta) > 500:
            return {'error': 'Shift exceeds 500 mm bound', 'results': results}
        before = [xyz(d.Location.Curve.GetEndPoint(i)) for i in (0, 1)]
        if max(abs(before[i][j] - pair['expected_ends_mm'][i][j])
               for i in (0, 1) for j in (0, 1, 2)) > 0.01:
            return {'error': 'Duct changed since scan', 'results': results}
        refs = connections(d)
        group = DB.TransactionGroup(doc, 'Temporary alignment verification - rollback')
        group.Start()
        t = DB.Transaction(doc, 'Batch alignment test - always rollback')
        capture = CaptureFailures()
        try:
            t.Start()
            failure_options = t.GetFailureHandlingOptions()
            failure_options.SetClearAfterRollback(True)
            failure_options.SetFailuresPreprocessor(capture)
            t.SetFailureHandlingOptions(failure_options)
            # Exercise an actual movement even for an already aligned duct.
            axis = pair['axis']
            perturb = DB.XYZ(0, 0, 10.0 / 304.8) if abs(axis[2]) < 0.9 else DB.XYZ(10.0 / 304.8, 0, 0)
            DB.ElementTransformUtils.MoveElement(doc, d.Id, perturb)
            doc.Regenerate()
            row['perturbed_mm'] = [xyz(d.Location.Curve.GetEndPoint(i)) for i in (0, 1)]
            row['connections_perturbed'] = connections(d)
            move = DB.XYZ(*[float(v) / 304.8 for v in delta]) - perturb
            DB.ElementTransformUtils.MoveElement(doc, d.Id, move)
            doc.Regenerate()
            row['aligned_mm'] = [xyz(d.Location.Curve.GetEndPoint(i)) for i in (0, 1)]
            row['connections_before'] = refs
            row['connections_aligned'] = connections(d)
            row['regeneration_succeeded'] = True
            row['transaction_status'] = str(t.Commit())
            row['failures'] = capture.messages
            if row['transaction_status'] == 'Committed':
                row['connections_aligned'] = connections(d)
                row['aligned_mm'] = [xyz(d.Location.Curve.GetEndPoint(i)) for i in (0, 1)]
        except Exception as ex:
            row['error'] = str(ex)
        finally:
            if t.GetStatus() == DB.TransactionStatus.Started:
                t.RollBack()
            t.Dispose()
            if group.GetStatus() == DB.TransactionStatus.Started:
                row['rollback_status'] = str(group.RollBack())
            group.Dispose()
        restored = doc.GetElement(DB.ElementId(int(pair['duct_id'])))
        row['restored_mm'] = [xyz(restored.Location.Curve.GetEndPoint(i)) for i in (0, 1)]
        row['connections_restored'] = connections(restored)
        row['before_mm'] = before
        results.append(row)
        result_path = r'F:\Hoa Le\1. Working\Tools\MEP_Align_Round_Duct_3D_ver1\MEP_Align_Round_Duct_3D_ver1\batch-live-results.json'
        with open(result_path, 'w') as output:
            json.dump({'mode': 'Perturb 10 mm, align, process commit failures, always rollback outer transaction group.',
                       'results': results}, output)
        if row.get('rollback_status') != 'RolledBack':
            return {'error': 'Rollback not confirmed; stopped', 'results': results}
    return {'mode': 'Perturb 10 mm then align, regenerate, rollback. No final commit tested.',
            'results': results}
