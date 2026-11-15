// Interactive 3D board view (WPF Viewport3D, hosted inside the WinForms window).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace HwInspector
{
    class Board3DView : Grid
    {
        public event Action<BoardComponent> Selected;

        class PartVisual { public Model3DGroup Group; public SolidColorBrush Glow; }

        readonly Viewport3D vp = new Viewport3D();
        readonly PerspectiveCamera cam = new PerspectiveCamera { FieldOfView = 38, NearPlaneDistance = 2, FarPlaneDistance = 8000 };
        readonly Model3DGroup scene = new Model3DGroup();
        readonly Model3DGroup cards = new Model3DGroup();
        readonly Model3DGroup cardHolder = new Model3DGroup();
        readonly Dictionary<GeometryModel3D, BoardComponent> hitMap = new Dictionary<GeometryModel3D, BoardComponent>();
        readonly Dictionary<BoardComponent, PartVisual> visuals = new Dictionary<BoardComponent, PartVisual>();
        readonly TextBlock hoverText = new TextBlock();
        readonly TextBlock hint = new TextBlock();

        BoardLayout layout;
        double yaw, pitch, dist;
        Point3D target;
        BoardComponent hover, selected;
        Point downPos, lastPos;
        bool rotating, panning, moved;

        static readonly Color HoverGlow = Color.FromRgb(48, 48, 40);
        static readonly Color SelectGlow = Color.FromRgb(110, 85, 0);

        public Board3DView()
        {
            Background = new RadialGradientBrush(Color.FromRgb(0x3A, 0x41, 0x4C), Color.FromRgb(0x14, 0x16, 0x1A)) { RadiusX = 0.9, RadiusY = 0.9 };
            vp.Camera = cam;
            var root = new Model3DGroup();
            root.Children.Add(new AmbientLight(Color.FromRgb(85, 85, 92)));
            root.Children.Add(new DirectionalLight(Color.FromRgb(205, 205, 200), new Vector3D(-0.35, -1, -0.55)));
            root.Children.Add(new DirectionalLight(Color.FromRgb(70, 75, 90), new Vector3D(0.6, -0.4, 0.8)));
            root.Children.Add(scene);
            root.Children.Add(cardHolder);
            cardHolder.Children.Add(cards);
            vp.Children.Add(new ModelVisual3D { Content = root });
            Children.Add(vp);

            hoverText.Foreground = Brushes.White;
            hoverText.FontSize = 15;
            hoverText.FontWeight = FontWeights.SemiBold;
            hoverText.Margin = new Thickness(12, 10, 12, 0);
            hoverText.VerticalAlignment = VerticalAlignment.Top;
            hoverText.HorizontalAlignment = HorizontalAlignment.Left;
            hoverText.IsHitTestVisible = false;
            hoverText.TextWrapping = TextWrapping.Wrap;
            Children.Add(hoverText);

            hint.Text = "Left-drag: rotate   ·   Right-drag: pan   ·   Wheel: zoom   ·   Click: details   ·   Double-click: focus";
            hint.Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
            hint.FontSize = 12;
            hint.Margin = new Thickness(12, 0, 12, 10);
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.HorizontalAlignment = HorizontalAlignment.Left;
            hint.IsHitTestVisible = false;
            Children.Add(hint);

            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseUp += OnUp;
            MouseWheel += OnWheel;
            MouseLeave += (s, e) => { if (!rotating && !panning) SetHover(null); };
        }

        // ------------------------------------------------------------ public API
        public void Load(BoardLayout L)
        {
            layout = L;
            scene.Children.Clear();
            cards.Children.Clear();
            hitMap.Clear();
            visuals.Clear();
            hover = selected = null;
            foreach (var c in L.Parts)
            {
                var g = BuildPart(c);
                if (c.IsCard) cards.Children.Add(g); else scene.Children.Add(g);
            }
            ResetView();
        }

        public bool ShowCards
        {
            get { return cardHolder.Children.Count > 0; }
            set
            {
                cardHolder.Children.Clear();
                if (value) cardHolder.Children.Add(cards);
                else if (selected != null && selected.IsCard) Select(null);
            }
        }

        public void ResetView()
        {
            target = new Point3D(0, 0, 0);
            yaw = 0.38; pitch = 1.12;
            target = new Point3D(10, 0, 8);
            dist = Math.Max(layout == null ? 300 : layout.Width, layout == null ? 300 : layout.Height) * 1.9;
            UpdateCamera();
        }

        public void TopView()
        {
            target = new Point3D(0, 0, 0);
            yaw = 0; pitch = 1.5607;
            dist = Math.Max(layout.Width, layout.Height) * 1.55;
            UpdateCamera();
        }

        public void FrontView()
        {
            target = new Point3D(0, 0, 0);
            yaw = 0; pitch = 0.32;
            dist = Math.Max(layout.Width, layout.Height) * 1.7;
            UpdateCamera();
        }

        public void Focus(BoardComponent c)
        {
            if (c == null || layout == null) return;
            target = new Point3D(c.X + c.W / 2 - layout.Width / 2, c.Z + c.H / 2, c.Y + c.D / 2 - layout.Height / 2);
            dist = Math.Max(70, Math.Max(c.W, Math.Max(c.D, c.H)) * 2.6);
            UpdateCamera();
        }

        // ------------------------------------------------------------ camera
        void UpdateCamera()
        {
            double cp = Math.Cos(pitch);
            var dir = new Vector3D(cp * Math.Sin(yaw), Math.Sin(pitch), cp * Math.Cos(yaw));
            cam.Position = target + dir * dist;
            cam.LookDirection = -dir * dist;
            cam.UpDirection = new Vector3D(0, 1, 0);
        }

        void OnDown(object s, MouseButtonEventArgs e)
        {
            downPos = lastPos = e.GetPosition(this);
            moved = false;
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
            {
                var c = HitAt(downPos);
                if (c != null) { Select(c); Focus(c); }
                return;
            }
            if (e.ChangedButton == MouseButton.Left) rotating = true;
            else panning = true;
            CaptureMouse();
        }

        void OnMove(object s, MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (rotating || panning)
            {
                double dx = p.X - lastPos.X, dy = p.Y - lastPos.Y;
                lastPos = p;
                if (Math.Abs(p.X - downPos.X) + Math.Abs(p.Y - downPos.Y) > 4) moved = true;
                if (rotating)
                {
                    yaw -= dx * 0.008;
                    pitch = Math.Max(0.06, Math.Min(1.5607, pitch + dy * 0.008));
                }
                else
                {
                    double k = dist * 0.0016;
                    var right = new Vector3D(Math.Cos(yaw), 0, -Math.Sin(yaw));
                    var fwd = new Vector3D(-Math.Sin(yaw), 0, -Math.Cos(yaw));
                    target -= right * dx * k;
                    target += fwd * dy * k / Math.Max(0.3, Math.Sin(pitch));
                }
                UpdateCamera();
                return;
            }
            SetHover(HitAt(p));
        }

        void OnUp(object s, MouseButtonEventArgs e)
        {
            bool wasRot = rotating;
            rotating = panning = false;
            ReleaseMouseCapture();
            if (wasRot && !moved && e.ChangedButton == MouseButton.Left)
                Select(HitAt(e.GetPosition(this)));
        }

        void OnWheel(object s, MouseWheelEventArgs e)
        {
            dist *= e.Delta > 0 ? 0.87 : 1.15;
            dist = Math.Max(40, Math.Min(3000, dist));
            UpdateCamera();
        }

        // ------------------------------------------------------------ picking
        BoardComponent HitAt(Point p)
        {
            BoardComponent found = null;
            VisualTreeHelper.HitTest(vp, null, new HitTestResultCallback(r =>
            {
                var rr = r as RayMeshGeometry3DHitTestResult;
                if (rr == null) return HitTestResultBehavior.Continue;
                var gm = rr.ModelHit as GeometryModel3D;
                BoardComponent c;
                if (gm != null && hitMap.TryGetValue(gm, out c))
                {
                    found = c.Clickable ? c : null;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            }), new PointHitTestParameters(p));
            return found;
        }

        void SetGlow(BoardComponent c)
        {
            PartVisual v;
            if (c == null || !visuals.TryGetValue(c, out v)) return;
            v.Glow.Color = c == selected ? SelectGlow : c == hover ? HoverGlow : Colors.Black;
        }

        void SetHover(BoardComponent c)
        {
            if (c == hover) return;
            var old = hover;
            hover = c;
            SetGlow(old);
            SetGlow(c);
            Cursor = c != null ? Cursors.Hand : Cursors.Arrow;
            var shown = c ?? selected;
            hoverText.Text = shown == null ? (layout != null ? layout.Title + "  ·  " + layout.FormFactor : "") :
                             shown.Name + (shown.Estimated ? "   (position estimated)" : "");
        }

        public void Select(BoardComponent c)
        {
            var old = selected;
            selected = c;
            PartVisual v;
            if (old != null && visuals.TryGetValue(old, out v)) v.Group.Transform = Transform3D.Identity;
            if (c != null && visuals.TryGetValue(c, out v) && c.Kind != "pcb") v.Group.Transform = new TranslateTransform3D(0, c.IsCard ? 6 : 3, 0);
            SetGlow(old);
            SetGlow(c);
            hoverText.Text = c == null ? layout.Title + "  ·  " + layout.FormFactor : c.Name;
            if (Selected != null) Selected(c);
        }

        // ------------------------------------------------------------ geometry
        static Color C(uint argb) { return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb); }

        static Color Shade(Color c, double f)
        {
            return Color.FromArgb(c.A, (byte)Math.Min(255, c.R * f), (byte)Math.Min(255, c.G * f), (byte)Math.Min(255, c.B * f));
        }

        static Material Mat(Brush b, Brush glow, bool shiny)
        {
            var g = new MaterialGroup();
            g.Children.Add(new DiffuseMaterial(b));
            if (shiny) g.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 35));
            g.Children.Add(new EmissiveMaterial(glow));
            return g;
        }

        static void Quad(MeshGeometry3D m, Point3D a, Point3D b, Point3D c, Point3D d, Vector3D outward, Point ta, Point tb, Point tc, Point td)
        {
            var n = Vector3D.CrossProduct(b - a, c - a);
            if (Vector3D.DotProduct(n, outward) < 0)
            {
                var t = b; b = d; d = t;
                var u = tb; tb = td; td = u;
            }
            int i = m.Positions.Count;
            m.Positions.Add(a); m.Positions.Add(b); m.Positions.Add(c); m.Positions.Add(d);
            for (int k = 0; k < 4; k++) m.Normals.Add(outward);
            m.TextureCoordinates.Add(ta); m.TextureCoordinates.Add(tb); m.TextureCoordinates.Add(tc); m.TextureCoordinates.Add(td);
            m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i + 2);
            m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 2); m.TriangleIndices.Add(i + 3);
        }

        static void Q(MeshGeometry3D m, Point3D a, Point3D b, Point3D c, Point3D d, Vector3D n)
        {
            Quad(m, a, b, c, d, n, new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1));
        }

        static MeshGeometry3D BoxSides(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            var m = new MeshGeometry3D();
            Q(m, new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y0, z1), new Point3D(x0, y0, z1), new Vector3D(0, -1, 0));
            Q(m, new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x0, y1, z1), new Vector3D(0, 0, 1));
            Q(m, new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0), new Point3D(x0, y1, z0), new Vector3D(0, 0, -1));
            Q(m, new Point3D(x1, y0, z0), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0), new Vector3D(1, 0, 0));
            Q(m, new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1), new Point3D(x0, y1, z0), new Vector3D(-1, 0, 0));
            m.Freeze();
            return m;
        }

        static MeshGeometry3D TopFace(double x0, double z0, double x1, double z1, double y)
        {
            var m = new MeshGeometry3D();
            Quad(m, new Point3D(x0, y, z0), new Point3D(x1, y, z0), new Point3D(x1, y, z1), new Point3D(x0, y, z1), new Vector3D(0, 1, 0),
                 new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1));
            m.Freeze();
            return m;
        }

        static void CylinderMeshes(double cx, double cz, double r, double y0, double y1, out MeshGeometry3D side, out MeshGeometry3D top)
        {
            const int N = 32;
            side = new MeshGeometry3D();
            top = new MeshGeometry3D();
            var center = new Point3D(cx, y1, cz);
            for (int i = 0; i < N; i++)
            {
                double a0 = 2 * Math.PI * i / N, a1 = 2 * Math.PI * (i + 1) / N;
                double c0 = Math.Cos(a0), s0 = Math.Sin(a0), c1 = Math.Cos(a1), s1 = Math.Sin(a1);
                var p0 = new Point3D(cx + r * c0, y0, cz + r * s0);
                var p1 = new Point3D(cx + r * c1, y0, cz + r * s1);
                var q1 = new Point3D(cx + r * c1, y1, cz + r * s1);
                var q0 = new Point3D(cx + r * c0, y1, cz + r * s0);
                double am = (a0 + a1) / 2;
                Q(side, p0, p1, q1, q0, new Vector3D(Math.Cos(am), 0, Math.Sin(am)));
                // top fan triangle (degenerate quad)
                Quad(top, center, q0, q1, q1, new Vector3D(0, 1, 0),
                     new Point(0.5, 0.5), new Point(0.5 + 0.5 * c0, 0.5 + 0.5 * s0), new Point(0.5 + 0.5 * c1, 0.5 + 0.5 * s1), new Point(0.5 + 0.5 * c1, 0.5 + 0.5 * s1));
            }
            side.Freeze();
            top.Freeze();
        }

        static ImageSource LabelTexture(Color bg, Color fg, string text, double wmm, double dmm, bool round)
        {
            double ppm = Math.Min(7, 1400 / Math.Max(wmm, dmm));
            int pw = Math.Max(8, (int)(wmm * ppm)), ph = Math.Max(8, (int)(dmm * ppm));
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, pw, ph));
                if (!round) dc.DrawRectangle(null, new Pen(new SolidColorBrush(Shade(bg, 1.35)), Math.Max(1, ppm * 0.35)), new Rect(0, 0, pw, ph));
                if (!string.IsNullOrEmpty(text))
                {
                    bool vertical = ph > pw * 1.8;
                    double boxW = (vertical ? ph : pw) * (round ? 0.75 : 0.92), boxH = (vertical ? pw : ph) * 0.8;
                    var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                    double size = Math.Min(boxH * 0.75, 7 * ppm);
                    var brush = new SolidColorBrush(fg);
#pragma warning disable 618
                    var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, brush);
                    if (ft.Width > boxW)
                    {
                        size = size * boxW / ft.Width;
                        ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, brush);
                    }
#pragma warning restore 618
                    if (vertical)
                    {
                        dc.PushTransform(new TranslateTransform(pw / 2.0, ph / 2.0));
                        dc.PushTransform(new RotateTransform(-90));
                        dc.DrawText(ft, new Point(-ft.Width / 2, -ft.Height / 2));
                        dc.Pop();
                        dc.Pop();
                    }
                    else dc.DrawText(ft, new Point((pw - ft.Width) / 2, (ph - ft.Height) / 2));
                }
            }
            var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        Model3DGroup BuildPart(BoardComponent c)
        {
            var grp = new Model3DGroup();
            var glow = new SolidColorBrush(Colors.Black);
            var col = C(c.Color);
            double x0 = c.X - layout.Width / 2, x1 = x0 + c.W;
            double z0 = c.Y - layout.Height / 2, z1 = z0 + c.D;
            double y0 = c.Z, y1 = c.Z + c.H;

            var sideMat = Mat(new SolidColorBrush(Shade(col, 0.72)), glow, c.Shiny);
            var topMat = Mat(new ImageBrush(LabelTexture(col, C(c.TextColor), c.Label, c.W, c.D, c.Cylinder)) { Stretch = Stretch.Fill }, glow, c.Shiny);

            MeshGeometry3D sideMesh, topMesh;
            if (c.Cylinder)
                CylinderMeshes(x0 + c.W / 2, z0 + c.D / 2, Math.Min(c.W, c.D) / 2, y0, y1, out sideMesh, out topMesh);
            else
            {
                sideMesh = BoxSides(x0, y0, z0, x1, y1, z1);
                topMesh = TopFace(x0, z0, x1, z1, y1);
            }
            var gs = new GeometryModel3D(sideMesh, sideMat);
            var gt = new GeometryModel3D(topMesh, topMat);
            grp.Children.Add(gs);
            grp.Children.Add(gt);
            hitMap[gs] = c;
            hitMap[gt] = c;
            visuals[c] = new PartVisual { Group = grp, Glow = glow };
            return grp;
        }
    }
}
