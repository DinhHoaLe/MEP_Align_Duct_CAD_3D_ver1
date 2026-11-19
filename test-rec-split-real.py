from pyrevit.api import DB
from System.Reflection import Assembly,BindingFlags
from System import Array,Object,Activator,Exception as NetException
asm=Assembly.LoadFrom(inputs['dll'])
flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static
ns='MEP_Align_Round_Duct_3D_ver1.'
ft=asm.GetType(ns+'DuctFrame');rt=asm.GetType(ns+'CadReader');cmd=asm.GetType(ns+'AlignDuctCommand')
d=doc.GetElement(DB.ElementId(17872873))
before=[d.Location.Curve.GetEndPoint(i) for i in [0,1]]
frame=ft.GetMethod('Create').Invoke(None,Array[Object]([d]))
mid=d.Location.Curve.Evaluate(.5,True)
cad=doc.GetElement(DB.ElementId(18466354))
o=DB.Options();o.ComputeReferences=True
root=next(g for g in cad.get_Geometry(o) if isinstance(g,DB.GeometryInstance))
tr=root.Transform
solid=next(g for g in root.GetSymbolGeometry() if isinstance(g,DB.Solid) and g.Faces.Size>10000)
parts=DB.SolidUtils.SplitVolumes(solid)
boxes=[p for p in parts if p.Faces.Size==6]
def centre(p):
 b=p.GetBoundingBox()
 return tr.OfPoint(b.Transform.OfPoint((b.Min+b.Max)/2))
part=min(boxes,key=lambda p:centre(p).DistanceTo(mid))
face=max(list(part.Faces),key=lambda f:f.Area)
uv=face.GetBoundingBox()
hit=tr.OfPoint(face.Evaluate((uv.Min+uv.Max)/2))
reader=Activator.CreateInstance(rt,Array[Object]([doc,frame,hit]))
rt.GetMethod('ReadRectBody',flags).Invoke(reader,Array[Object]([solid,tr,'']))
axes=list(rt.GetField('found',flags).GetValue(reader))
row={'parts':len(parts),'axes':len(axes),'diagnostics':rt.GetProperty('Diagnostics').GetValue(reader)}
if len(axes)==1:
 group=DB.TransactionGroup(doc,'Test split CAD alignment - rollback')
 group.Start()
 try:
  status=cmd.GetMethod('ApplyAlignment',flags).Invoke(None,Array[Object]([uiapp,uidoc,d,frame,axes[0]]))
  row['move']=str(status)
  row['width_mm']=d.get_Parameter(DB.BuiltInParameter.RBS_CURVE_WIDTH_PARAM).AsDouble()*304.8
  row['height_mm']=d.get_Parameter(DB.BuiltInParameter.RBS_CURVE_HEIGHT_PARAM).AsDouble()*304.8
 except NetException as ex:row['move_rejected']=str(ex.InnerException or ex).split('   at ')[0]
 finally:group.RollBack()
 row['rollback_error_mm']=max(doc.GetElement(d.Id).Location.Curve.GetEndPoint(i).DistanceTo(before[i])*304.8 for i in [0,1])
result=row
