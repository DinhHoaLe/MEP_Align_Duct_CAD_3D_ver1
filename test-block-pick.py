from pyrevit.api import DB
from System import Array,Object,Activator,Int32,Double
from System.Collections.Generic import List
from System.Reflection import Assembly,BindingFlags
from System.IO import File
asm=Assembly.Load(File.ReadAllBytes(inputs['dll']))
flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance
ft=asm.GetType('MEP_Align_Round_Duct_3D_ver1.DuctFrame')
rt=asm.GetType('MEP_Align_Round_Duct_3D_ver1.CadReader')
def make_reader(hit,tr):
 f=Activator.CreateInstance(ft,True)
 for name,value in [('A',tr.OfPoint(DB.XYZ(0,0,0))),('B',tr.OfPoint(DB.XYZ(6,0,0))),('Axis',tr.OfVector(DB.XYZ.BasisX)),('WidthAxis',tr.OfVector(DB.XYZ.BasisY)),('HeightAxis',tr.OfVector(DB.XYZ.BasisZ)),('Width',Double(2)),('Height',Double(1))]:
  ft.GetField(name,flags).SetValue(f,value)
 return Activator.CreateInstance(rt,flags,None,Array[Object]([doc,f,hit]),None)
loop=DB.CurveLoop()
pts=[DB.XYZ(0,-1,-.5),DB.XYZ(0,1,-.5),DB.XYZ(0,1,.5),DB.XYZ(0,-1,.5)]
for i in range(4):loop.Append(DB.Line.CreateBound(pts[i],pts[(i+1)%4]))
solid=DB.GeometryCreationUtilities.CreateExtrusionGeometry(List[DB.CurveLoop]([loop]),DB.XYZ.BasisX,6)
center=DB.Line.CreateBound(DB.XYZ(0,0,0),DB.XYZ(6,0,0))
edge=DB.Line.CreateBound(DB.XYZ(0,1,.5),DB.XYZ(6,1,.5))
rows=[]
rot=DB.Transform.CreateRotation(DB.XYZ.BasisZ,.7)
tr=DB.Transform.CreateTranslation(DB.XYZ(10,20,3)).Multiply(rot)
for name,objects,hit,transform,layer,depth,expected in [
 ('click body away from center',[solid,center,edge],DB.XYZ(3,1,0),DB.Transform.Identity,'DUCT',2,1),
 ('transformed block',[solid,center,edge],tr.OfPoint(DB.XYZ(3,1,0)),tr,'DUCT',2,1),
 ('click outline with center layer',[center,DB.Line.CreateBound(DB.XYZ(0,0,0),DB.XYZ(0,1,0))],DB.XYZ(0,.5,0),DB.Transform.Identity,'CENTER',2,1),
 ('reject arbitrary outline',[edge],DB.XYZ(3,1,.5),DB.Transform.Identity,'DUCT',2,0),
 ('do not search entire DWG',[center,DB.Line.CreateBound(DB.XYZ(0,0,0),DB.XYZ(0,1,0))],DB.XYZ(0,.5,0),DB.Transform.Identity,'CENTER',1,0),
 ('reject bounding box only',[solid,center],DB.XYZ(3,4,0),DB.Transform.Identity,'DUCT',2,0)]:
 reader=make_reader(hit,transform)
 res=rt.GetMethod('FindRectBlock',flags).Invoke(reader,Array[Object]([List[DB.GeometryObject](objects),transform,layer,Int32(depth)]))
 axes=res.GetType().GetField('Axes',flags).GetValue(res)
 error=None
 if axes.Count:
  print(name)
  a=list(axes)[0]
  at=asm.GetType('MEP_Align_Round_Duct_3D_ver1.AxisCandidate')
  o=at.GetField('Origin',flags).GetValue(a)
  v=at.GetField('Axis',flags).GetValue(a)
  error=o.Subtract(transform.OfPoint(DB.XYZ.Zero)).CrossProduct(v).GetLength()*304.8
 passed=axes.Count==expected and (expected==0 or error<.01)
 rows.append({'case':name,'axes':axes.Count,'axis_error_mm':error,'pass':passed})
result=rows
