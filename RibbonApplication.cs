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
                rectButton.ToolTip = "Chọn duct chữ nhật → pick trực tiếp đường tim CAD thẳng, song song với duct.";
                rectButton.LongDescription = "Căn XYZ theo Line/Polyline thẳng được pick. Không cần layer riêng. " +
                    "Không tự tìm tim từ thân khối CAD. Nhiều trục phù hợp: dừng và pick lại. " +
                    "Dịch duct cùng fitting, kiểm tra giữ kết nối. Các đoạn nối khác có thể đổi chiều dài/hướng. Không xoay hoặc đổi size duct chọn. Lỗi/mất kết nối: rollback. Undo một lần để hoàn tác.";
                rectButton.Image = Icon("RecAlign16.png");
                rectButton.LargeImage = Icon("RecAlign32.png");
            }
            if (!panel.GetItems().Any(i => i.Name == "HoL.BlockAlign.v170"))
            {
                var block = (PushButton)panel.AddItem(new PushButtonData("HoL.BlockAlign.v170", "Block\nAlign",
                    Assembly.GetExecutingAssembly().Location, typeof(BlockAlignCommand).FullName));
                block.ToolTip = "Chon block CAD, sau do chon family: can goc XYZ, giu nguyen goc xoay.";
                block.LongDescription = "Lay goc block con gan nhat chua hinh hoc duoc click. " +
                    "Khong dung tam bounding box. Duct/pipe noi vao tu dieu chinh; kiem tra giu ket noi, loi thi rollback. Undo mot lan de hoan tac.";
                block.Image = BlockIcon(16);
                block.LargeImage = BlockIcon(32);
            }
            return Result.Succeeded;
        }
        internal static System.Windows.Media.ImageSource BlockIcon(int size)
        {
            var drawing = new System.Windows.Media.DrawingGroup();
            using (var dc = drawing.Open())
            {
                var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.DodgerBlue, 2);
                dc.DrawRectangle(null, pen, new System.Windows.Rect(6, 6, 18, 18));
                dc.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.OrangeRed, 2),
                    new System.Windows.Point(15, 15), new System.Windows.Point(30, 15));
                dc.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.ForestGreen, 2),
                    new System.Windows.Point(15, 15), new System.Windows.Point(15, 1));
                dc.DrawEllipse(System.Windows.Media.Brushes.DodgerBlue, null, new System.Windows.Point(15, 15), 3, 3);
            }
            var image = new System.Windows.Media.DrawingImage(drawing); image.Freeze(); return image;
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
