from pyrevit.api import DB
from System.Reflection import Assembly, BindingFlags
from System import Array,Object,Exception as NetException
asm=Assembly.LoadFrom(r'D:\2_Revit\6_Working\00. Revit_API\Project\MEP_Align_Round_Duct_3D_ver1\MEP_Align_Round_Duct_3D_ver1\MEP_Align_Round_Duct_3D_ver1\bin\BlockAlignConnectedTest2023\HoL.BlockAlign.ConnectedTest2023.dll')
method=asm.GetType('MEP_Align_Round_Duct_3D_ver1.BlockAlignCommand').GetMethod('MoveToOrigin',BindingFlags.Static|BindingFlags.NonPublic)
samples=[]
counts={}
for f in DB.FilteredElementCollector(doc).OfClass(DB.FamilyInstance):
 if f.Pinned or f.GroupId!=DB.ElementId.InvalidElementId or f.SuperComponent or not f.MEPModel: continue
 mgr=f.MEPModel.ConnectorManager
 if not mgr: continue
 ports=[c for c in mgr.Connectors if c.ConnectorType!=DB.ConnectorType.Logical and c.Domain in [DB.Domain.DomainHvac,DB.Domain.DomainPiping] and c.IsConnected]
 if not ports: continue
 domain=str(ports[0].Domain)
 if counts.get(domain,0)>=2: continue
 counts[domain]=counts.get(domain,0)+1
 samples.append(f.Id)
rows=[]
for eid in samples:
 f=doc.GetElement(eid)
 before=f.GetTransform()
 target=before.Origin+DB.XYZ(10.0/304.8,0,0)
 group=DB.TransactionGroup(doc,'Temporary connected Block Align test')
 group.Start()
 row={'id':eid.IntegerValue}
 try:
  method.Invoke(None,Array[Object]([uiapp,doc,f,target]))
  after=doc.GetElement(eid).GetTransform()
  row['origin_error_mm']=after.Origin.DistanceTo(target)*304.8
  row['rotation_unchanged']=all(a.IsAlmostEqualTo(b) for a,b in [(before.BasisX,after.BasisX),(before.BasisY,after.BasisY),(before.BasisZ,after.BasisZ)])
  row['connections_validated']=True
 except NetException as ex:
  row['rejected']=str(ex.InnerException or ex).split('   at ')[0]
 finally:
  group.RollBack()
 row['rollback_error_mm']=doc.GetElement(eid).GetTransform().Origin.DistanceTo(before.Origin)*304.8
 rows.append(row)
result={'sample_domains':counts,'tests':rows}
