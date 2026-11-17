using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MEP_Align_Round_Duct_3D_ver1
{
    public class RibbonApplication : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string tab = "HoL Tools", panelName = "CAD Align";
            try { application.CreateRibbonTab(tab); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            RibbonPanel panel = application.GetRibbonPanels(tab).FirstOrDefault(p => p.Name == panelName)
                ?? application.CreateRibbonPanel(tab, panelName);
            if (!panel.GetItems().Any(i => i.Name == "HoL.RoundDuctAlign.v152"))
            {
            PushButtonData data = new PushButtonData("HoL.RoundDuctAlign.v152", "Round\nAlign",
                Assembly.GetExecutingAssembly().Location, typeof(AlignDuctCommand).FullName);
            PushButton button = (PushButton)panel.AddItem(data);
            button.ToolTip = "Căn tim ống gió tròn Revit theo khối CAD 3D được pick.";
            button.LongDescription = "Chọn ống tròn thẳng → pick mặt/cạnh thân ống CAD → căn XYZ ngay. " +
                "Không cần pick đường tim. Không xoay hoặc kéo dài ống. " +
                "Điểm pick mơ hồ hoặc lỗi Revit: dừng/rollback. Undo một lần để hoàn tác. " +
                "Các nhánh/phụ kiện nối có thể thay đổi theo ống. Dùng view 3D để pick rõ khối CAD.";
            button.Image = Icon("RoundAlign16.png");
            button.LargeImage = Icon("RoundAlign32.png");
            }
            if (!panel.GetItems().Any(i => i.Name == "HoL.RectDuctAlign.v152"))
            {
                PushButtonData rectData = new PushButtonData("HoL.RectDuctAlign.v152", "Rec\nAlign",
                    Assembly.GetExecutingAssembly().Location, typeof(AlignRectDuctCommand).FullName);
                PushButton rectButton = (PushButton)panel.AddItem(rectData);
                rectButton.ToolTip = "Căn tim ống gió chữ nhật Revit theo block hoặc center line CAD.";
                rectButton.LongDescription = "Chọn duct chữ nhật thẳng → click thân block CAD → tự tìm center line trong block. " +
                    "Không cần pick trực tiếp đường tim; căn XYZ ngay. " +
                    "Block chỉ có nét rời cần layer CENTER/CENTRE/CL/TIM. Nhiều block/trục phù hợp: dừng và pick lại. " +
                    "Dịch duct cùng fitting, kiểm tra giữ kết nối. Các đoạn nối khác có thể đổi chiều dài/hướng. Không xoay hoặc đổi size duct chọn. Lỗi/mất kết nối: rollback. Undo một lần để hoàn tác.";
                rectButton.Image = Icon("RecAlign16.png");
                rectButton.LargeImage = Icon("RecAlign32.png");
            }
            return Result.Succeeded;
        }
        private static BitmapImage Icon(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HoL.RoundAlign." + name))
            {
                if (stream == null) throw new InvalidOperationException("Missing ribbon icon: " + name);
                BitmapImage image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }
        }
        public Result OnShutdown(UIControlledApplication application) { return Result.Succeeded; }
    }
    public class DuctAlignAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet categories)
        {
            Document doc = app.ActiveUIDocument == null ? null : app.ActiveUIDocument.Document;
            // Availability is evaluated in a read-only API context; do not use IsReadOnly here.
            return doc != null && !doc.IsFamilyDocument;
        }
    }
}
