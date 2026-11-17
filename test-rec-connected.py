from pyrevit.api import DB
from System import Array, Object, Activator, Exception as NetException
from System.IO import File
from System.Reflection import Assembly, BindingFlags
asm=Assembly.Load(File.ReadAllBytes(inputs['dll']))
ns='MEP_Align_Round_Duct_3D_ver1.'
ft=asm.GetType(ns+'DuctFrame'); at=asm.GetType(ns+'AxisCandidate'); cmd=asm.GetType(ns+'AlignDuctCommand')
flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance
rows=[]
for eid in inputs['ids']:
    group=DB.TransactionGroup(doc,'Temporary connected alignment test'); group.Start()
    try:
        d=doc.GetElement(DB.ElementId(eid))
        before=[d.Location.Curve.GetEndPoint(i) for i in [0,1]]
        frame=ft.GetMethod('Create',flags).Invoke(None,Array[Object]([d]))
        axis=ft.GetField('Axis',flags).GetValue(frame); mid=ft.GetProperty('Mid',flags).GetValue(frame)
        shift=axis.CrossProduct(DB.XYZ.BasisZ)
        if shift.GetLength()<0.01: shift=axis.CrossProduct(DB.XYZ.BasisX)
        shift=shift.Normalize().Multiply(10.0/304.8)
        target=Activator.CreateInstance(at,True)
        at.GetField('Axis',flags).SetValue(target,axis); at.GetField('Origin',flags).SetValue(target,mid.Add(shift))
        result_value=cmd.GetMethod('ApplyAlignment',flags).Invoke(None,Array[Object]([uiapp,uidoc,d,frame,target]))
        actual=doc.GetElement(DB.ElementId(eid)).Location.Curve
        error=max(actual.GetEndPoint(i).DistanceTo(before[i].Add(shift))*304.8 for i in [0,1])
        rows.append({'id':eid,'result':str(result_value),'translation_error_mm':error})
    except NetException as ex:
        messages=[ex.Message]
        for n in range(8):
            ex=getattr(ex,'InnerException',None)
            if ex is None: break
            messages.append(str(ex.Message))
        rows.append({'id':eid,'errors':messages})
    finally:
        group.RollBack()
        restored=doc.GetElement(DB.ElementId(eid)).Location.Curve
        rows[-1]['rollback_error_mm']=max(restored.GetEndPoint(i).DistanceTo(before[i])*304.8 for i in [0,1])
result=rows
