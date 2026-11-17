from pyrevit.api import DB
from System import Array,Object,Activator,Double,Exception as NetException
from System.IO import File
from System.Reflection import Assembly,BindingFlags
asm=Assembly.Load(File.ReadAllBytes(r'D:\2_Revit\6_Working\00. Revit_API\Project\MEP_Align_Round_Duct_3D_ver1\MEP_Align_Round_Duct_3D_ver1\MEP_Align_Round_Duct_3D_ver1\bin\BlockPickTest2023\HoL.DuctAlign.BlockPickTest2023.dll'))
f=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance
ft=asm.GetType('MEP_Align_Round_Duct_3D_ver1.DuctFrame');rt=asm.GetType('MEP_Align_Round_Duct_3D_ver1.CadReader');at=asm.GetType('MEP_Align_Round_Duct_3D_ver1.AxisCandidate')
cad=doc.GetElement(DB.ElementId(18466354));opts=DB.Options();opts.ComputeReferences=True
samples=[];count=[0]
def layer(g):
 st=doc.GetElement(g.GraphicsStyleId)
 return st.GraphicsStyleCategory.Name if st and st.GraphicsStyleCategory else ''
def walk(gs,tr,depth):
 if len(samples)>=3:return
 objs=list(gs);centers=[g for g in objs if isinstance(g,DB.Line) and g.IsBound and layer(g)=='DUCT_RECT_CENTER'];others=[g for g in objs if isinstance(g,DB.Line) and g.IsBound and layer(g)!='DUCT_RECT_CENTER']
 if centers and others:
  c=centers[0];a=tr.OfPoint(c.GetEndPoint(0));b=tr.OfPoint(c.GetEndPoint(1));axis=b.Subtract(a).Normalize()
  for other in others:
   hit=tr.OfPoint(other.Evaluate(.5,True))
   if hit.Subtract(a).CrossProduct(axis).GetLength()>1.0/304.8:
    samples.append((a,b,hit,layer(other),depth,other.Reference.ConvertToStableRepresentation(doc)));break
 for g in objs:
  count[0]+=1
  if count[0]>30000 or len(samples)>=3:return
  if isinstance(g,DB.GeometryInstance):
   sub=g.GetSymbolGeometry()
   try:walk(sub,tr.Multiply(g.Transform),depth+1)
   finally:sub.Dispose()
ge=cad.get_Geometry(opts)
try:walk(ge,DB.Transform.Identity,0)
finally:ge.Dispose()
rows=[]
for a,b,hit,hitlayer,depth,stable in samples:
 axis=b.Subtract(a).Normalize();w=axis.CrossProduct(DB.XYZ.BasisZ)
 if w.GetLength()<.01:w=axis.CrossProduct(DB.XYZ.BasisX)
 w=w.Normalize();h=axis.CrossProduct(w).Normalize()
 frame=Activator.CreateInstance(ft,True)
 for name,value in [('A',a),('B',b),('Axis',axis),('WidthAxis',w),('HeightAxis',h),('Width',Double(.6)),('Height',Double(.3))]:ft.GetField(name,f).SetValue(frame,value)
 reader=Activator.CreateInstance(rt,f,None,Array[Object]([doc,frame,hit]),None)
 method=next(m for m in rt.GetMethods(f) if m.Name=='Read' and len(m.GetParameters())==2)
 try: axes=list(method.Invoke(reader,Array[Object]([cad,DB.Reference.ParseFromStableRepresentation(doc,stable)])))
 except NetException as ex:
  rows.append({'depth':depth,'error':ex.InnerException.Message if ex.InnerException else ex.Message});continue
 errors=[at.GetField('Origin',f).GetValue(x).Subtract(a).CrossProduct(axis).GetLength()*304.8 for x in axes]
 rows.append({'depth':depth,'clicked_layer':hitlayer,'click_distance_to_center_mm':hit.Subtract(a).CrossProduct(axis).GetLength()*304.8,'axes':len(axes),'axis_errors_mm':errors,'pass':len(axes)==1 and errors[0]<.1})
result=rows
