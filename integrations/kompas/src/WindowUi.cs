// Общее для окон расширения: DPI потока, масштаб, шрифты и цвета макета, заготовки контролов.
// Размеры в коде — пиксели макета при 96 точках на дюйм; Ui.S пересчитывает их под экран.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NormaCad.Kompas
{
    /// <summary>
    /// Поток окна объявляется DPI-aware: при масштабе 125–150% Windows не растягивает окно картинкой,
    /// текст остаётся чётким, а размеры окно считает само (Ui.S). SetThreadDpiAwarenessContext есть
    /// с Windows 10 1607; где её нет, вызов тихо пропускается и окно растягивает Windows.
    /// </summary>
    internal static class Dpi
    {
        // DPI_AWARENESS_CONTEXT_SYSTEM_AWARE: масштаб основного экрана. Per-monitor WinForms .NET 4
        // не поддерживает — на экране с другим масштабом Windows растянет окно сама.
        private static readonly IntPtr SystemAware = new IntPtr(-2);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        /// <summary>Вызывать в потоке окна до создания первого окна.</summary>
        public static void EnterThread()
        {
            // Снимки «как при 100%» на экране 150%: окно рисуется в 96 точках, растягивает его Windows
            if (Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_DPI") == "unaware") return;
            try { SetThreadDpiAwarenessContext(SystemAware); }
            catch (EntryPointNotFoundException) { } // до Windows 10 1607
            catch (Exception) { }
        }
    }

    /// <summary>Масштаб, шрифты, цвета и заготовки контролов окна. Создаётся в потоке окна.</summary>
    internal sealed class Ui : IDisposable
    {
        // Theme belongs to the extension's UI thread, never to the KOMPAS process.
        [ThreadStatic] private static bool darkTheme;
        private static readonly Color[] LightPalette = Colors(new int[] {
            0xF4F6FB, 0xFFFFFF, 0x0F172A, 0x4B5563, 0x2563EB,
            0xB4232F, 0x884608, 0x09664D, 0xE4E8F0 });
        private static readonly Color[] DarkPalette = Colors(new int[] {
            0x0B1220, 0x131C2D, 0xF1F5F9, 0xA9B5C7, 0x86B9FF,
            0xFF959B, 0xFFC075, 0x69DFB7, 0x344056 });
        private static Color[] Colors(int[] values)
        {
            Color[] colors = new Color[values.Length];
            for (int i = 0; i < values.Length; i++)
                colors[i] = Color.FromArgb(255, Color.FromArgb(values[i]));
            return colors;
        }
        private static Color ThemeColor(int index) { return (darkTheme ? DarkPalette : LightPalette)[index]; }
        public static bool IsDark { get { return darkTheme; } }
        public static void SetDark(bool dark) { darkTheme = dark; }
        public static Color Background { get { return ThemeColor(0); } }
        public static Color Surface { get { return ThemeColor(1); } }
        public static Color Text { get { return ThemeColor(2); } }
        public static Color Gray { get { return ThemeColor(3); } }
        public static Color Link { get { return ThemeColor(4); } }
        public static Color Critical { get { return ThemeColor(5); } }
        public static Color Warning { get { return ThemeColor(6); } }
        public static Color Good { get { return ThemeColor(7); } }
        public static Color Frame { get { return ThemeColor(8); } }
        public static Color Line { get { return ThemeColor(8); } }
        public static Color Rule { get { return darkTheme ? Color.FromArgb(0x2A, 0x35, 0x50) : Line; } }
        public static readonly Color Accent = Color.FromArgb(0xF9, 0x73, 0x16);
        private static readonly Color PrimaryInk = Color.FromArgb(0x0F, 0x17, 0x2A);

        private static Color BlendTint(Color accent, Color surface)
        {
            return Color.FromArgb((accent.R * 18 + surface.R * 82) / 100,
                (accent.G * 18 + surface.G * 82) / 100,
                (accent.B * 18 + surface.B * 82) / 100);
        }

        public static Color Tint(Color accent) { return BlendTint(accent, Surface); }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

        internal static void ThemeCaption(Form form)
        {
            if (!form.IsHandleCreated) return;
            int value = IsDark ? 1 : 0;
            try { DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        public static void ApplyTheme(Control root, bool dark)
        {
            if (root == null || root.IsDisposed) return;
            if (root.InvokeRequired)
            {
                root.Invoke(new MethodInvoker(delegate { ApplyTheme(root, dark); }));
                return;
            }
            Color[] previous = darkTheme ? DarkPalette : LightPalette;
            Color[] next = dark ? DarkPalette : LightPalette;
            Func<Color, Color> map = delegate(Color color)
            {
                for (int i = 0; i < previous.Length; i++)
                    if (color.ToArgb() == previous[i].ToArgb()) return next[i];
                for (int i = 5; i <= 7; i++)
                    if (color.ToArgb() == BlendTint(previous[i], previous[1]).ToArgb())
                        return BlendTint(next[i], next[1]);
                if (color.ToArgb() == BlendTint(Accent, previous[1]).ToArgb())
                    return BlendTint(Accent, next[1]);
                return color;
            };
            root.SuspendLayout();
            try
            {
                Recolor(root, map, next);
                SetDark(dark);
                Form form = root as Form;
                if (form != null) ThemeCaption(form);
            }
            finally { root.ResumeLayout(true); root.Invalidate(true); }
        }

        private static void Recolor(Control control, Func<Color, Color> map, Color[] palette)
        {
            // Read inherited colors while the parent still has its original palette.
            foreach (Control child in control.Controls) Recolor(child, map, palette);
            control.BackColor = map(control.BackColor);
            control.ForeColor = map(control.ForeColor);
            // Native editors otherwise retain Windows' white input surface.
            if (control is TextBoxBase || control is ComboBox || control is ListBox)
            {
                control.BackColor = palette[1];
                control.ForeColor = palette[2];
            }
            LinkLabel link = control as LinkLabel;
            if (link != null)
            {
                link.LinkColor = map(link.LinkColor);
                link.ActiveLinkColor = map(link.ActiveLinkColor);
                link.VisitedLinkColor = map(link.VisitedLinkColor);
                link.DisabledLinkColor = palette[3];
            }
            Button button = control as Button;
            if (button != null)
            {
                button.FlatAppearance.BorderColor = map(button.FlatAppearance.BorderColor);
                button.FlatAppearance.MouseOverBackColor = map(button.FlatAppearance.MouseOverBackColor);
                button.FlatAppearance.MouseDownBackColor = map(button.FlatAppearance.MouseDownBackColor);
                if (button.BackColor.ToArgb() == Accent.ToArgb()) button.ForeColor = PrimaryInk;
            }
            FramePanel panel = control as FramePanel;
            if (panel != null) panel.FrameColor = map(panel.FrameColor);
            FrameTable table = control as FrameTable;
            if (table != null) table.FrameColor = map(table.FrameColor);
            InlineText inline = control as InlineText;
            if (inline != null) inline.Recolor(map);
            control.Invalidate();
        }

        /// <summary>Пикселей экрана на пиксель макета: 1 при 100%, 1,5 при 150%.</summary>
        public readonly float Scale;
        /// <summary>Segoe UI 9 — основной текст (12 px макета).</summary>
        public readonly Font Font;
        public readonly Font Bold;
        /// <summary>Итог отчёта: Segoe UI 10 жирный (13 px макета).</summary>
        public readonly Font Large;
        /// <summary>«Журнал для поддержки»: Segoe UI 8,25 (11 px макета).</summary>
        public readonly Font Small;

        public Ui()
        {
            float dpi = 96f;
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
            }
            catch (Exception) { }
            Scale = dpi / 96f;
            Font = new Font("Segoe UI", 9f);
            Bold = new Font(Font, FontStyle.Bold);
            Large = new Font("Segoe UI", 10f, FontStyle.Bold);
            Small = new Font("Segoe UI", 8.25f);
        }

        public void Dispose()
        {
            Font.Dispose();
            Bold.Dispose();
            Large.Dispose();
            Small.Dispose();
        }

        public int S(int px)
        {
            return (int)Math.Round(px * Scale);
        }

        public Size S(int width, int height)
        {
            return new Size(S(width), S(height));
        }

        public Padding P(int all)
        {
            return new Padding(S(all));
        }

        public Padding P(int left, int top, int right, int bottom)
        {
            return new Padding(S(left), S(top), S(right), S(bottom));
        }

        /// <summary>
        /// Надпись по размеру текста, без полей. В ячейке TableLayoutPanel с Dock = Fill переносится
        /// по ширине колонки; «&amp;» в именах файлов не превращается в подчёркивание.
        /// </summary>
        public Label MakeLabel(string text, Color color, Font font)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.UseCompatibleTextRendering = false;
            l.UseMnemonic = false;
            l.Margin = Padding.Empty;
            l.Padding = Padding.Empty;
            l.ForeColor = color;
            l.Font = font ?? Font;
            l.Text = text ?? "";
            return l;
        }

        public Label MakeLabel(string text)
        {
            return MakeLabel(text, Text, null);
        }

        /// <summary>Ссылка с подчёркиванием, цвет не меняется после нажатия.</summary>
        public LinkLabel MakeLink(string text, Color color, Font font)
        {
            LinkLabel l = new LinkLabel();
            l.AutoSize = true;
            l.UseCompatibleTextRendering = false;
            l.UseMnemonic = false;
            l.Margin = Padding.Empty;
            l.Padding = Padding.Empty;
            l.Font = font ?? Font;
            l.LinkColor = color;
            l.ActiveLinkColor = color;
            l.VisitedLinkColor = color;
            l.LinkBehavior = LinkBehavior.AlwaysUnderline;
            l.Text = text ?? "";
            return l;
        }

        public LinkLabel MakeLink(string text)
        {
            return MakeLink(text, Link, null);
        }

        /// <summary>Кнопка не уже 88×26 макета, ширина растёт под текст.</summary>
        public Button MakeButton(string text)
        {
            Button b = new Button();
            b.UseCompatibleTextRendering = false;
            b.UseMnemonic = false;
            b.UseVisualStyleBackColor = false;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = Background;
            b.BackColor = Surface;
            b.ForeColor = Text;
            b.Font = Font;
            b.Cursor = Cursors.Hand;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = S(88, 32);
            b.Padding = P(8, 0, 8, 0);
            b.Margin = Padding.Empty;
            b.Text = text ?? "";
            return b;
        }

        public PictureBox MakeBrandMark()
        {
            PictureBox mark = new PictureBox();
            mark.Size = S(32, 32);
            mark.SizeMode = PictureBoxSizeMode.Zoom;
            mark.TabStop = false;
            using (System.IO.Stream stream = typeof(Ui).Assembly.GetManifestResourceStream("NormaCAD.Mark.png"))
            using (Image image = Image.FromStream(stream))
                mark.Image = new Bitmap(image);
            mark.Disposed += delegate { mark.Image.Dispose(); };
            return mark;
        }
        public void MakePrimary(Button button)
        {
            button.BackColor = Accent;
            button.ForeColor = PrimaryInk;
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xEA, 0x58, 0x0C);
            button.Font = Bold;
        }

        /// <summary>Выпадающий список без ввода; width — в пикселях макета.</summary>
        public ComboBox MakeCombo(int width)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Font = Font;
            c.BackColor = Surface;
            c.ForeColor = Text;
            c.FlatStyle = FlatStyle.Flat;
            c.Margin = Padding.Empty;
            c.Width = S(width);
            return c;
        }

        /// <summary>Таблица в одну колонку на всю ширину; строки — по высоте содержимого.</summary>
        public TableLayoutPanel MakeColumn()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.ColumnCount = 1;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.Margin = Padding.Empty;
            t.Padding = Padding.Empty;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            return t;
        }

        /// <summary>Добавить строку по высоте содержимого; контрол растягивается по ширине колонки.</summary>
        public static void AddRow(TableLayoutPanel t, Control c)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 0, t.RowStyles.Count - 1);
            t.RowCount = t.RowStyles.Count;
        }

        /// <summary>Строка-заполнитель: забирает остаток высоты.</summary>
        public static void AddFill(TableLayoutPanel t, Control c)
        {
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 0, t.RowStyles.Count - 1);
            t.RowCount = t.RowStyles.Count;
        }
    }

    /// <summary>
    /// Панель с рамкой в 1 px заданного цвета (область результата): у Panel.BorderStyle рамка только
    /// системного цвета. Содержимое — один вид с Dock = Fill; Padding по умолчанию 1 px — под рамку.
    /// </summary>
    internal class FramePanel : Panel
    {
        private Color frame = Ui.Frame;

        public FramePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Ui.Surface;
            Padding = new Padding(1);
        }

        public Color FrameColor
        {
            get { return frame; }
            set { frame = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(frame))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // Keep the native progress range and accessibility support, paint in the brand palette.
    internal sealed class BrandProgressBar : ProgressBar
    {
        public BrandProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            AccessibleName = "Ход проверки";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Ui.Line);
            int width = ClientSize.Width * (Value - Minimum) / Math.Max(1, Maximum - Minimum);
            using (Brush fill = new SolidBrush(Ui.Accent))
                e.Graphics.FillRectangle(fill, 0, 0, width, ClientSize.Height);
        }
    }

    /// <summary>
    /// Таблица с рамкой в 1 px (панель аккаунта, карточка отчёта): раскладка и AutoSize — как у
    /// TableLayoutPanel, рамка рисуется по краю, внутри Padding.
    /// </summary>
    internal class FrameTable : TableLayoutPanel
    {
        private Color frame = Ui.Line;

        public FrameTable()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Ui.Surface;
            Margin = Padding.Empty;
        }

        public Color FrameColor
        {
            get { return frame; }
            set { frame = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(frame))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>
    /// Группа с подписью на рамке (как GroupBox в макете: рамка #D0D0D0, подпись разрывает линию). Своя, а не
    /// GroupBox: у того рамка и поля системные, а AutoSize с переносимым текстом внутри ненадёжен.
    /// Содержимое — строки таблицы в одну колонку. Над рамкой оставлено 8 px под подпись: отступ от соседей
    /// задаёт Margin.Top на эти 8 px меньше, чем в макете.
    /// </summary>
    internal class GroupFrame : TableLayoutPanel
    {
        private readonly Ui ui;
        private readonly string title;
        private const TextFormatFlags TitleFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;

        public GroupFrame(Ui ui, string title)
        {
            this.ui = ui;
            this.title = title ?? "";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Ui.Surface;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = Padding.Empty;
            // 8 px подписи над рамкой + рамка 1 + отступ 12 сверху; по бокам 1 + 10, снизу 8 + 1
            Padding = new Padding(ui.S(10) + 1, ui.S(8) + 1 + ui.S(12), ui.S(10) + 1, ui.S(8) + 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int top = ui.S(8);
            using (Pen pen = new Pen(Ui.Line))
                e.Graphics.DrawRectangle(pen, 0, top, Width - 1, Height - 1 - top);
            if (title == "") return;
            Size ts = TextRenderer.MeasureText(e.Graphics, title, ui.Font, new Size(int.MaxValue, int.MaxValue), TitleFlags);
            int left = ui.S(9);
            using (SolidBrush back = new SolidBrush(BackColor))
                e.Graphics.FillRectangle(back, left, 0, ts.Width + ui.S(6), ui.S(16));
            TextRenderer.DrawText(e.Graphics, title, ui.Font,
                new Rectangle(left + ui.S(3), 0, ts.Width, ui.S(16)), Ui.Text, TitleFlags);
        }
    }

    /// <summary>Блок с чертой сверху (серая подсказка внизу области: «Окно можно закрыть…»).</summary>
    internal class RuleBox : TableLayoutPanel
    {
        public RuleBox(Ui ui)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Ui.Surface;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = Padding.Empty;
            Padding = new Padding(0, 1 + ui.S(8), 0, 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Ui.Rule))
                e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }

    /// <summary>
    /// Текст с переносом по словам, где часть слов жирная: «Как исправить: укажите массу…» (первые слова жирные).
    /// Label так не умеет, RichTextBox тяжёл и не растёт по высоте. Рисуется через TextRenderer, как Label без
    /// совместимого вывода текста; высота по заданной ширине — GetPreferredSize, поэтому в ячейке
    /// TableLayoutPanel он ведёт себя как Label.
    /// </summary>
    internal sealed class InlineText : Control
    {
        private struct Token
        {
            public string Text;
            public Font Font;
            public Color Color;
            public bool SpaceBefore;
            public int Width;
        }

        private struct Placed
        {
            public int X;
            public int Y;
            public int Index;
        }

        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        private readonly Ui ui;
        private readonly List<Token> tokens = new List<Token>();
        private readonly int spaceWidth;
        private readonly int lineHeight;

        public InlineText(Ui ui)
        {
            this.ui = ui;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            AutoSize = true;
            TabStop = false;
            Margin = Padding.Empty;
            BackColor = Ui.Surface;
            Font = ui.Font;
            Size huge = new Size(int.MaxValue, int.MaxValue);
            // Ширина пробела: у TextRenderer пробел на краю строки обрезается, поэтому через разность
            spaceWidth = TextRenderer.MeasureText("a a", ui.Font, huge, Flags).Width
                - TextRenderer.MeasureText("aa", ui.Font, huge, Flags).Width;
            lineHeight = Math.Max(ui.Font.Height, ui.Bold.Height) + ui.S(1);
        }

        /// <summary>Добавить кусок текста. Пробел в начале куска отделяет его от предыдущего.</summary>
        public void Add(string text, bool bold, Color color)
        {
            string t = text ?? "";
            Font font = bold ? ui.Bold : ui.Font;
            bool lead = t.Length > 0 && t[0] == ' ';
            bool first = true;
            foreach (string word in t.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Token k = new Token();
                k.Text = word;
                k.Font = font;
                k.Color = color;
                k.SpaceBefore = first ? lead : true;
                k.Width = TextRenderer.MeasureText(word, font, new Size(int.MaxValue, int.MaxValue), Flags).Width;
                tokens.Add(k);
                first = false;
            }
        }

        internal void Recolor(Func<Color, Color> map)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                Token token = tokens[i];
                token.Color = map(token.Color);
                tokens[i] = token;
            }
        }

        private int Flow(int width, List<Placed> placed, out int widest)
        {
            int x = 0, y = 0;
            widest = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                Token k = tokens[i];
                int gap = k.SpaceBefore && x > 0 ? spaceWidth : 0;
                if (x > 0 && x + gap + k.Width > width)
                {
                    x = 0;
                    y += lineHeight;
                    gap = 0;
                }
                if (placed != null)
                {
                    Placed p = new Placed();
                    p.X = x + gap;
                    p.Y = y;
                    p.Index = i;
                    placed.Add(p);
                }
                x += gap + k.Width;
                widest = Math.Max(widest, x);
            }
            return tokens.Count == 0 ? 0 : y + lineHeight;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int widest;
            int limit = Reflow.Width(this, proposedSize);
            int height = Flow(limit, null, out widest);
            if (Environment.GetEnvironmentVariable("NORMACAD_WRAPDBG") == "1" && tokens.Count > 3 && tokens[3].Text.StartsWith("АБВГ")) Console.WriteLine("InlineText.pref proposed=" + proposedSize + " Width=" + Width + " limit=" + limit + " -> h=" + height + " Height=" + Height);
            return new Size(limit == int.MaxValue ? widest : limit, height);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            int widest;
            Reflow.AfterResize(this, Flow(Math.Max(1, ClientSize.Width), null, out widest));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            List<Placed> placed = new List<Placed>();
            int widest;
            Flow(Math.Max(1, ClientSize.Width), placed, out widest);
            foreach (Placed p in placed)
            {
                Token k = tokens[p.Index];
                TextRenderer.DrawText(e.Graphics, k.Text, k.Font, new Point(p.X, p.Y), k.Color, BackColor, Flags);
            }
        }
    }

    /// <summary>
    /// Строка элементов слева направо с переносом на следующую, когда не помещаются (серые части строки аккаунта,
    /// шапка карточки, фильтры). FlowLayoutPanel с AutoSize внутри AutoSize-таблицы запоминает высоту по первой, ещё
    /// не окончательной ширине, поэтому перенос считается здесь: высота — по заданной ширине (GetPreferredSize),
    /// элементы в строке выравниваются по вертикали по центру. Отступы Margin элементов учитываются.
    /// </summary>
    internal sealed class WrapRow : Control
    {
        public WrapRow()
        {
            AutoSize = true;
            TabStop = false;
            Margin = Padding.Empty;
            Padding = Padding.Empty;
        }

        private struct Slot
        {
            public Control Control;
            public Size Size;
            public int X;
            public int Row;
        }

        private Size Arrange(int width, bool apply)
        {
            List<Slot> slots = new List<Slot>();
            List<int> heights = new List<int>();
            int x = 0, row = 0, rowHeight = 0, widest = 0;
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                Padding m = c.Margin;
                int available = width == int.MaxValue ? int.MaxValue : Math.Max(1, width - m.Horizontal);
                Size size = c.AutoSize ? c.GetPreferredSize(new Size(available, 0)) : c.Size;
                if (c.AutoSize && size.Width > available)
                {
                    size.Width = available;
                    size.Height = c.GetPreferredSize(new Size(available, 0)).Height;
                }
                int w = size.Width + m.Horizontal;
                if (x > 0 && x + w > width)
                {
                    heights.Add(rowHeight);
                    row++;
                    x = 0;
                    rowHeight = 0;
                }
                Slot slot = new Slot();
                slot.Control = c;
                slot.Size = size;
                slot.X = x + m.Left;
                slot.Row = row;
                slots.Add(slot);
                x += w;
                rowHeight = Math.Max(rowHeight, size.Height + m.Vertical);
                widest = Math.Max(widest, x);
            }
            if (slots.Count > 0) heights.Add(rowHeight);

            if (apply)
            {
                int[] tops = new int[heights.Count];
                int y = 0;
                for (int i = 0; i < heights.Count; i++)
                {
                    tops[i] = y;
                    y += heights[i];
                }
                foreach (Slot s in slots)
                {
                    Padding m = s.Control.Margin;
                    int free = heights[s.Row] - s.Size.Height - m.Vertical;
                    s.Control.SetBounds(s.X, tops[s.Row] + m.Top + free / 2, s.Size.Width, s.Size.Height);
                }
            }
            int total = 0;
            foreach (int h in heights) total += h;
            return new Size(widest, total);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int limit = Reflow.Width(this, proposedSize);
            Size s = Arrange(limit, false);
            return new Size(limit == int.MaxValue ? s.Width : Math.Min(limit, s.Width), s.Height);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Reflow.AfterResize(this, Arrange(Math.Max(1, ClientSize.Width), false).Height);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Arrange(Math.Max(1, ClientSize.Width), true);
        }
    }

    /// <summary>
    /// Высота переносимых элементов (WrapRow, InlineText) в AutoSize-таблице. TableLayoutPanel опрашивает GetPreferredSize
    /// «пробными» ширинами (1, 0, 171…) и берёт высоту из такого опроса, а не из окончательной ширины колонки.
    /// Поэтому элемент считает высоту по собственной ширине, которую таблица уже назначила, а когда ширина
    /// меняется и высота перестаёт совпадать — просит родителя пересчитать раскладку (после текущего прохода).
    /// </summary>
    internal static class Reflow
    {
        /// <summary>Ширина для расчёта: своя, если уже назначена; иначе пробная из запроса; иначе без ограничения.</summary>
        public static int Width(Control c, Size proposed)
        {
            if (c.Width > 1) return c.Width;
            return proposed.Width > 1 && proposed.Width < 32000 ? proposed.Width : int.MaxValue;
        }

        public static void AfterResize(Control c, int wantedHeight)
        {
            if (wantedHeight == c.Height || c.Parent == null || !c.IsHandleCreated) return;
            try
            {
                c.BeginInvoke(new MethodInvoker(delegate
                {
                    if (c.IsDisposed || c.Parent == null) return;
                    c.Parent.PerformLayout();
                }));
            }
            catch (InvalidOperationException) { }
        }
    }
}
