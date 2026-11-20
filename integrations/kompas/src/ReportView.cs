// Отчёт в области результата (состояние 6): итог, строки-оговорки, фильтры и карточки замечаний
// во FlowLayoutPanel с прокруткой. Данные и тексты — из CheckView.cs (Cards, CardFilter).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NormaCad.Kompas
{
    /// <summary>
    /// Карточка замечания: цветная полоса слева (важность), шапка «Важность Лист N источник», заголовок, стандарт,
    /// скрытое по умолчанию пояснение, способы исправить. Ширину задаёт отчёт (Min/MaximumSize), высота — по тексту.
    /// </summary>
    internal sealed class CardPanel : FrameTable
    {
        private readonly Ui ui;
        private readonly Card card;
        private readonly Label detail;
        private readonly LinkLabel toggle;
        private bool expanded;

        public CardPanel(Ui ui, Card card)
        {
            this.ui = ui;
            this.card = card;
            ColumnCount = 2;
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ui.S(3)));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            RowCount = 1;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // Рамка 1 px рисуется по краю, полоса и тело лежат внутри неё
            Padding = new Padding(1);
            Margin = new Padding(0, 0, 0, ui.S(10));

            Color accent = card.Severity == "critical" ? Ui.Critical : card.Severity == "ok" ? Ui.Good : Ui.Warning;
            Panel bar = new Panel();
            bar.BackColor = accent;
            bar.Dock = DockStyle.Fill;
            bar.Margin = Padding.Empty;
            Controls.Add(bar, 0, 0);

            TableLayoutPanel body = ui.MakeColumn();
            body.Padding = ui.P(14, 12, 14, 12);
            body.BackColor = Ui.Surface;
            body.Dock = DockStyle.Fill;

            // Шапка: «Критическое» цветом важности, «Лист 1», серый источник; расстояние между — 10 px
            WrapRow head = new WrapRow();
            Label sev = ui.MakeLabel(card.SeverityLabel, accent, ui.Bold);
            sev.Padding = ui.P(7, 2, 7, 2);
            sev.BackColor = Ui.Tint(accent);
            sev.Margin = new Padding(0, 0, ui.S(10), ui.S(2));
            head.Controls.Add(sev);
            if (card.PageLabel != "")
            {
                Label page = ui.MakeLabel(card.PageLabel, Ui.Text, ui.Bold);
                page.Margin = new Padding(0, 0, ui.S(10), 0);
                head.Controls.Add(page);
            }
            head.Controls.Add(ui.MakeLabel(card.SourceLabel, Ui.Gray, null));
            AddBody(body, head);

            AddBody(body, ui.MakeLabel(card.Title, Ui.Text, ui.Large));
            if (card.Standard != "") AddBody(body, ui.MakeLabel(card.Standard, Ui.Gray, null));

            detail = ui.MakeLabel(card.Detail);
            detail.Visible = false;
            if (card.Detail != "") AddBody(body, detail);

            if (card.Fixes.Count == 1)
            {
                InlineText fix = new InlineText(ui);
                fix.Add(card.FixesCaption, true, Ui.Text);
                fix.Add(" " + card.Fixes[0], false, Ui.Text);
                AddBody(body, fix);
            }
            else if (card.Fixes.Count > 1)
            {
                AddBody(body, ui.MakeLabel(card.FixesCaption, Ui.Text, ui.Bold));
                TableLayoutPanel list = new TableLayoutPanel();
                list.ColumnCount = 2;
                list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ui.S(12)));
                list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                list.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
                list.AutoSize = true;
                list.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                list.BackColor = Ui.Surface;
                list.Margin = Padding.Empty;
                for (int i = 0; i < card.Fixes.Count; i++)
                {
                    Label dot = ui.MakeLabel("•");
                    Label text = ui.MakeLabel(card.Fixes[i]);
                    text.Dock = DockStyle.Fill;
                    list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    list.Controls.Add(dot, 0, i);
                    list.Controls.Add(text, 1, i);
                    list.RowCount = i + 1;
                }
                AddBody(body, list);
            }

            toggle = ui.MakeLink("Пояснение");
            toggle.Margin = new Padding(0, ui.S(4), 0, 0);
            toggle.Anchor = AnchorStyles.Left;
            toggle.LinkClicked += delegate { SetExpanded(!expanded); };
            if (card.Detail != "")
            {
                body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                body.Controls.Add(toggle, 0, body.RowStyles.Count - 1);
                body.RowCount = body.RowStyles.Count;
            }

            Controls.Add(body, 1, 0);
        }

        /// <summary>Строки тела — по ширине, с промежутком 5 px между ними.</summary>
        private void AddBody(TableLayoutPanel body, Control c)
        {
            c.Margin = new Padding(0, 0, 0, ui.S(5));
            Ui.AddRow(body, c);
        }

        public Card Card { get { return card; } }

        public bool Expanded { get { return expanded; } }

        public bool HasDetail { get { return card.Detail != ""; } }

        public void SetExpanded(bool value)
        {
            if (card.Detail == "") return;
            expanded = value;
            SuspendLayout();
            detail.Visible = value;
            toggle.Text = value ? "Скрыть пояснение" : "Пояснение";
            ResumeLayout(true);
        }
    }

    internal sealed class ReportView : TableLayoutPanel
    {
        private readonly Ui ui;
        private readonly List<Card> cards;
        private readonly List<CardPanel> panels = new List<CardPanel>();
        private readonly CardFilter filter = new CardFilter();
        private readonly ComboBox cbSeverity, cbPage, cbSource;
        private readonly Label lblCount;
        private readonly Label lblNone;
        private readonly LinkLabel resetFilters;
        private readonly FlowLayoutPanel flow;
        private int fitWidth = -1;
        private bool fitting;
        private bool resettingFilters;

        public ReportView(Ui ui, List<Card> cards, string verdict, List<string> notes)
        {
            this.ui = ui;
            this.cards = cards ?? new List<Card>();
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            BackColor = Ui.Background;
            Margin = Padding.Empty;
            Padding = Padding.Empty;

            Label v = ui.MakeLabel(verdict, Ui.Text, ui.Large);
            AddGap(v);
            if (notes != null)
                foreach (string n in notes) AddGap(ui.MakeLabel(n, Ui.Gray, null));

            if (this.cards.Count == 0)
            {
                // Только итог: замечаний нет или документ не проверен — фильтры и список не нужны
                RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                RowCount = RowStyles.Count;
                return;
            }

            // Фильтры и число показанных замечаний.
            TableLayoutPanel bar = new TableLayoutPanel();
            bar.ColumnCount = 1;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bar.RowCount = 2;
            bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.BackColor = Ui.Surface;
            bar.Margin = Padding.Empty;
            bar.Padding = ui.P(10, 8, 10, 8);

            WrapRow filters = new WrapRow();
            filters.Dock = DockStyle.Fill;
            cbSeverity = AddFilter(filters, "Важность:", 116, CardFilter.SeverityChoices(this.cards));
            cbPage = AddFilter(filters, "Лист:", 60, CardFilter.PageChoices(this.cards));
            cbSource = AddFilter(filters, "Источник:", 90, CardFilter.SourceChoices(this.cards));
            cbSeverity.SelectedIndexChanged += delegate { Refilter(); };
            cbPage.SelectedIndexChanged += delegate { Refilter(); };
            cbSource.SelectedIndexChanged += delegate { Refilter(); };
            bar.Controls.Add(filters, 0, 0);

            lblCount = ui.MakeLabel("", Ui.Gray, null);
            lblCount.Anchor = AnchorStyles.Left;
            lblCount.Margin = new Padding(0, 0, ui.S(12), 0);
            WrapRow summary = new WrapRow();
            summary.Dock = DockStyle.Fill;
            summary.Margin = new Padding(0, ui.S(4), 0, 0);
            summary.Controls.Add(lblCount);
            resetFilters = ui.MakeLink("Сбросить фильтры");
            resetFilters.LinkClicked += delegate { ResetFilters(); };
            summary.Controls.Add(resetFilters);
            bar.Controls.Add(summary, 0, 1);
            AddGap(bar);

            lblNone = ui.MakeLabel("Нет замечаний с такими параметрами.", Ui.Gray, null);
            lblNone.Visible = false;
            AddGap(lblNone);

            flow = new FlowLayoutPanel();
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoScroll = true;
            flow.BackColor = Ui.Background;
            flow.Margin = Padding.Empty;
            flow.Padding = new Padding(0, 0, ui.S(6), 0);
            flow.Dock = DockStyle.Fill;
            foreach (Card c in this.cards)
            {
                CardPanel p = new CardPanel(ui, c);
                panels.Add(p);
                flow.Controls.Add(p);
            }
            RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(flow, 0, RowStyles.Count - 1);
            RowCount = RowStyles.Count;

            // Колёсико мыши крутит список, как только указатель над ним
            flow.MouseEnter += delegate { if (!flow.ContainsFocus) flow.Focus(); };
            flow.ClientSizeChanged += delegate { FitCards(); };
            flow.HandleCreated += delegate { FitCards(); };
            Refilter();
            FitCards();
        }

        private void AddGap(Control c)
        {
            c.Margin = new Padding(c.Margin.Left, c.Margin.Top, c.Margin.Right, ui.S(6));
            Ui.AddRow(this, c);
        }

        private ComboBox AddFilter(WrapRow parent, string caption, int width, List<Choice> choices)
        {
            WrapRow pair = new WrapRow();
            pair.Margin = new Padding(0, 0, ui.S(12), ui.S(6));
            ComboBox cb = ui.MakeCombo(width);
            cb.AccessibleName = caption.TrimEnd(':');
            foreach (Choice c in choices) cb.Items.Add(c);
            cb.SelectedIndex = 0;
            // Учитываем шрифт и масштаб Windows: все варианты должны читаться целиком.
            int dropWidth = cb.Width;
            foreach (Choice c in choices)
                dropWidth = Math.Max(dropWidth, TextRenderer.MeasureText(c.ToString(), cb.Font).Width
                    + SystemInformation.VerticalScrollBarWidth + ui.S(8));
            cb.DropDownWidth = dropWidth;
            Label l = ui.MakeLabel(caption);
            l.Margin = new Padding(0, 0, ui.S(6), 0);
            pair.Controls.Add(l);
            pair.Controls.Add(cb);
            // Keep each caption and its selector together when the parent wraps to another line.
            pair.AutoSize = false;
            Size labelSize = l.GetPreferredSize(Size.Empty);
            pair.Size = new Size(labelSize.Width + l.Margin.Horizontal + cb.Width + cb.Margin.Horizontal,
                Math.Max(labelSize.Height + l.Margin.Vertical, cb.Height + cb.Margin.Vertical));
            parent.Controls.Add(pair);
            return cb;
        }

        private void Refilter()
        {
            if (resettingFilters) return;
            Choice sev = cbSeverity.SelectedItem as Choice;
            Choice page = cbPage.SelectedItem as Choice;
            Choice src = cbSource.SelectedItem as Choice;
            filter.Severity = sev == null ? "" : sev.Value;
            filter.Page = page == null ? "" : page.Value;
            filter.Source = src == null ? "" : src.Value;

            int shown = 0;
            flow.SuspendLayout();
            foreach (CardPanel p in panels)
            {
                bool match = filter.Match(p.Card);
                if (p.Visible != match) p.Visible = match;
                if (match) shown++;
            }
            flow.ResumeLayout(true);
            flow.AutoScrollPosition = Point.Empty;
            lblCount.Text = CardFilter.Counter(shown, cards.Count);
            lblNone.Visible = shown == 0;
            resetFilters.Visible = filter.Severity != "" || filter.Page != "" || filter.Source != "";
        }

        private void ResetFilters()
        {
            resettingFilters = true;
            try
            {
                cbSeverity.SelectedIndex = 0;
                cbPage.SelectedIndex = 0;
                cbSource.SelectedIndex = 0;
            }
            finally { resettingFilters = false; }
            Refilter();
        }

        /// <summary>Карточки — по ширине списка без полосы прокрутки; высоту каждая берёт из текста.</summary>
        private void FitCards()
        {
            if (fitting || flow == null) return;
            int w = Math.Max(1, flow.ClientSize.Width - flow.Padding.Horizontal);
            if (w == fitWidth) return;
            fitting = true;
            try
            {
                fitWidth = w;
                flow.SuspendLayout();
                foreach (CardPanel p in panels)
                {
                    p.MinimumSize = new Size(w, 0);
                    p.MaximumSize = new Size(w, 0);
                }
                flow.ResumeLayout(true);
            }
            finally
            {
                fitting = false;
            }
        }

        /// <summary>Для проверки вида: раскрыть пояснение у карточки по номеру (после сортировки и фильтра).</summary>
        internal void ExpandDetail(int index)
        {
            if (index >= 0 && index < panels.Count) panels[index].SetExpanded(true);
        }

        internal int CardCount { get { return panels.Count; } }
    }
}
