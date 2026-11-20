// Виды области результата, пока отчёта нет: подсказка (состояние 3) и ход проверки (состояния 4 и 5):
// группа «Проверено на ПК, без ИИ» и группа «Проверка на сервере». Тексты — из CheckView.cs.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NormaCad.Kompas
{
    /// <summary>Серая подсказка в пустой области (состояние 3 «Готово к проверке.»).</summary>
    internal sealed class HintView : TableLayoutPanel
    {
        public HintView(Ui ui, string text)
        {
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            BackColor = Ui.Surface;
            Margin = Padding.Empty;
            Padding = ui.P(14, 12, 14, 12);
            if (!string.IsNullOrEmpty(text))
            {
                Ui.AddRow(this, ui.MakeLabel(text, Ui.Gray, null));
            }
            // Остаток высоты — пустой строке: иначе таблица растягивает единственную строку
            RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            RowCount = RowStyles.Count;
        }
    }

    /// <summary>
    /// Ход проверки. Сверху — данные с ПК (развёрнутой группой, пока задание в очереди; свёрнутой строкой со ссылкой
    /// «Показать», когда оно пошло в работу), ниже — группа сервера: этапы, место в очереди, время. Внизу, когда
    /// задание в работе, — черта и подсказка, что окно можно закрыть. Перестраивается только когда что-то изменилось.
    /// </summary>
    internal sealed class ProgressView : TableLayoutPanel
    {
        private readonly Ui ui;
        private LocalFacts facts;
        private int factsVersion;
        private JobView job = JobView.Sending();
        // Выбор пользователя «Показать»/«Скрыть» для идущей проверки; null — пользователь не трогал
        private bool? userExpanded;
        private string signature = "\u0001";

        public ProgressView(Ui ui, LocalFacts facts)
        {
            this.ui = ui;
            this.facts = facts;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            BackColor = Ui.Surface;
            Margin = Padding.Empty;
            Padding = Padding.Empty;
            AutoScroll = true;
            Render();
        }

        /// <summary>Группа ПК развёрнута: пока задание не в работе — всегда, в работе — по выбору пользователя.</summary>
        public bool PcExpanded
        {
            get { return !job.Running || (userExpanded.HasValue && userExpanded.Value); }
        }

        public void SetFacts(LocalFacts value)
        {
            facts = value;
            factsVersion++;
            Render();
        }

        public void Update(JobView value)
        {
            job = value ?? JobView.Sending();
            Render();
        }

        private string Signature()
        {
            List<string> p = new List<string>();
            p.Add(factsVersion.ToString());
            p.Add(PcExpanded ? "1" : "0");
            p.Add(job.Status);
            foreach (StageRow s in job.Stages) p.Add(s.Name + "/" + s.State + "/" + (int)s.Kind);
            p.AddRange(job.Lines);
            p.Add(job.Hint);
            return string.Join("\u0001", p.ToArray());
        }

        private void Render()
        {
            string sig = Signature();
            if (sig == signature) return;
            signature = sig;

            SuspendLayout();
            foreach (Control c in Controls) c.Dispose();
            Controls.Clear();
            RowStyles.Clear();
            RowCount = 0;

            Label heading = ui.MakeLabel("Ход проверки", Ui.Text, ui.Large);
            heading.Margin = new Padding(0, 0, 0, ui.S(10));
            Ui.AddRow(this, heading);
            bool first = true;
            if (facts != null)
            {
                if (!FactsText.Readable(facts))
                {
                    Ui.AddRow(this, ui.MakeLabel(FactsText.Failed(facts), Ui.Gray, null));
                    first = false;
                }
                else if (job.Running)
                {
                    Ui.AddRow(this, PcSummary());
                    first = false;
                    if (PcExpanded)
                    {
                        Control g = PcGroup();
                        g.Margin = new Padding(0, ui.S(4), 0, 0);
                        Ui.AddRow(this, g);
                    }
                }
                else
                {
                    Control g = PcGroup();
                    g.Margin = new Padding(0, ui.S(2), 0, 0);
                    Ui.AddRow(this, g);
                    first = false;
                }
            }

            Control server = ServerGroup();
            // Первая группа в области — 10 px от края в макете, следующие — 12; 8 px из них занимает подпись на рамке
            server.Margin = new Padding(0, ui.S(first ? 2 : 10), 0, 0);
            Ui.AddRow(this, server);

            // Остаток высоты забирает пустая строка, подсказка уходит вниз области
            RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            RowCount = RowStyles.Count;

            if (job.Running)
            {
                RuleBox note = new RuleBox(ui);
                Ui.AddRow(note, ui.MakeLabel("Окно можно закрыть: проверка продолжится на сервере. "
                    + "Отчёт откроется здесь при следующем запуске и сохранится в истории на сайте.", Ui.Gray, null));
                Ui.AddRow(this, note);
            }
            ResumeLayout(true);
        }

        /// <summary>Строка над группой ПК в идущей проверке: «На ПК: 3 замечания…» и «Показать»/«Скрыть».</summary>
        private Control PcSummary()
        {
            WrapRow row = new WrapRow();
            Label text = ui.MakeLabel(FactsText.Collapsed(facts));
            text.Margin = new Padding(0, 0, ui.S(8), 0);
            row.Controls.Add(text);
            LinkLabel toggle = ui.MakeLink(PcExpanded ? "Скрыть" : "Показать");
            toggle.LinkClicked += delegate
            {
                userExpanded = !PcExpanded;
                Render();
            };
            row.Controls.Add(toggle);
            return row;
        }

        /// <summary>Группа «Проверено на ПК, без ИИ»: что прочитано, замечания по основной надписи, чего не охватили.</summary>
        private Control PcGroup()
        {
            GroupFrame g = new GroupFrame(ui, "Проверено на ПК, без ИИ");

            List<KeyValuePair<string, string>> rows = FactsText.Rows(facts);
            if (rows.Count > 0)
            {
                TableLayoutPanel kv = new TableLayoutPanel();
                kv.ColumnCount = 2;
                kv.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                kv.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                kv.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
                kv.AutoSize = true;
                kv.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                kv.BackColor = Ui.Surface;
                kv.Margin = Padding.Empty;
                for (int i = 0; i < rows.Count; i++)
                {
                    int bottom = i < rows.Count - 1 ? ui.S(5) : 0;
                    Label key = ui.MakeLabel(rows[i].Key, Ui.Gray, null);
                    key.Margin = new Padding(0, 0, ui.S(10), bottom);
                    key.Dock = DockStyle.Fill;
                    Label value = ui.MakeLabel(rows[i].Value);
                    value.Margin = new Padding(0, 0, 0, bottom);
                    value.Dock = DockStyle.Fill;
                    kv.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    kv.Controls.Add(key, 0, i);
                    kv.Controls.Add(value, 1, i);
                    kv.RowCount = i + 1;
                }
                Ui.AddRow(g, kv);
            }

            Label header = ui.MakeLabel(FactsText.RemarksHeader(facts), Ui.Text, ui.Bold);
            header.Margin = new Padding(0, rows.Count > 0 ? ui.S(6) : 0, 0, 0);
            Ui.AddRow(g, header);

            List<LocalRemark> remarks = new List<LocalRemark>();
            if (facts.Remarks != null)
                foreach (LocalRemark r in facts.Remarks) if (r != null) remarks.Add(r);
            if (remarks.Count > 0)
            {
                TableLayoutPanel list = new TableLayoutPanel();
                list.ColumnCount = 2;
                list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ui.S(18)));
                list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                list.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
                list.AutoSize = true;
                list.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                list.BackColor = Ui.Surface;
                list.Margin = new Padding(0, ui.S(3), 0, 0);
                for (int i = 0; i < remarks.Count; i++)
                {
                    int bottom = i < remarks.Count - 1 ? ui.S(2) : 0;
                    Label dot = ui.MakeLabel("•");
                    dot.Margin = new Padding(ui.S(7), 0, 0, bottom);
                    Label line = ui.MakeLabel(FactsText.RemarkLine(remarks[i]));
                    line.Margin = new Padding(0, 0, 0, bottom);
                    line.Dock = DockStyle.Fill;
                    list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    list.Controls.Add(dot, 0, i);
                    list.Controls.Add(line, 1, i);
                    list.RowCount = i + 1;
                }
                Ui.AddRow(g, list);
            }

            string note = (facts.Note ?? "").Trim();
            if (note != "")
            {
                Label n = ui.MakeLabel(note, Ui.Gray, null);
                n.Margin = new Padding(0, ui.S(3), 0, 0);
                Ui.AddRow(g, n);
            }
            return g;
        }

        /// <summary>Группа «Проверка на сервере»: этапы, место в очереди, время, нагрузка.</summary>
        private Control ServerGroup()
        {
            GroupFrame g = new GroupFrame(ui, "Проверка на сервере");

            if (job.Stages.Count > 0)
            {
                TableLayoutPanel stages = new TableLayoutPanel();
                stages.ColumnCount = 2;
                stages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f));
                stages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
                stages.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
                stages.AutoSize = true;
                stages.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                stages.BackColor = Ui.Surface;
                stages.Margin = new Padding(0, 0, 0, ui.S(6));
                for (int i = 0; i < job.Stages.Count; i++)
                {
                    StageRow s = job.Stages[i];
                    int bottom = i < job.Stages.Count - 1 ? ui.S(7) : 0;
                    Color nameColor = s.Kind == StageKind.Waiting ? Ui.Gray : Ui.Text;
                    Color stateColor = s.Kind == StageKind.Done ? Ui.Good : s.Kind == StageKind.Running ? Ui.Text : Ui.Gray;
                    Font font = s.Kind == StageKind.Running ? ui.Bold : null;
                    Label name = ui.MakeLabel(s.Name, nameColor, font);
                    name.Dock = DockStyle.Fill;
                    name.Margin = new Padding(0, 0, 0, bottom);
                    name.Padding = ui.P(7, 5, 4, 5);
                    Label state = ui.MakeLabel(s.State, stateColor, font);
                    state.Dock = DockStyle.Fill;
                    state.TextAlign = ContentAlignment.TopRight;
                    state.Padding = ui.P(4, 5, 7, 5);
                    Color stageBackground = s.Kind == StageKind.Running
                        ? Ui.Tint(Ui.Accent) : Ui.Background;
                    name.BackColor = stageBackground;
                    state.BackColor = stageBackground;
                    state.Margin = new Padding(ui.S(12), 0, 0, bottom);
                    stages.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    stages.Controls.Add(name, 0, i);
                    stages.Controls.Add(state, 1, i);
                    stages.RowCount = i + 1;
                }
                Ui.AddRow(g, stages);
            }

            foreach (string line in job.Lines) Ui.AddRow(g, ui.MakeLabel(line));
            if (job.Hint != "") Ui.AddRow(g, ui.MakeLabel(job.Hint, Ui.Gray, null));
            return g;
        }
    }
}
