// Вид сбоя в области результата — по макету fail(): по центру фраза, одна кнопка действия, ниже серая
// мелкая ссылка «Журнал для поддержки». Тексты и кнопки даёт Failure.Classify (ApiClient.cs).
using System;
using System.Drawing;
using System.Windows.Forms;

namespace NormaCad.Kompas
{
    internal sealed class FailureView : TableLayoutPanel
    {
        private readonly Ui ui;
        private readonly Label message;

        /// <summary>action == null или пустая подпись — кнопки нет (отказ без действия).</summary>
        public FailureView(Ui ui, string text, string actionLabel, EventHandler onAction, EventHandler onLog)
        {
            this.ui = ui;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            RowCount = 3;
            RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            BackColor = Ui.Surface;
            Margin = Padding.Empty;
            Padding = Padding.Empty;
            AutoScroll = true;

            // Содержимое по центру области: цепочка строк по высоте текста, посередине между двумя растяжками
            TableLayoutPanel box = new TableLayoutPanel();
            box.ColumnCount = 1;
            box.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            box.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            box.AutoSize = true;
            box.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            box.BackColor = Ui.Surface;
            box.Margin = Padding.Empty;
            box.Anchor = AnchorStyles.None;

            message = ui.MakeLabel(text);
            message.TextAlign = ContentAlignment.TopCenter;
            message.MaximumSize = new Size(ui.S(380), 0);
            message.Anchor = AnchorStyles.None;
            message.Margin = Padding.Empty;
            AddCentered(box, message);

            bool hasAction = onAction != null && !string.IsNullOrEmpty(actionLabel);
            if (hasAction)
            {
                Button b = ui.MakeButton(actionLabel);
                b.Margin = new Padding(0, ui.S(12), 0, 0);
                b.Click += onAction;
                AddCentered(box, b);
            }

            LinkLabel log = ui.MakeLink("Журнал для поддержки", Ui.Gray, ui.Small);
            // Мелкая ссылка отстоит от кнопки на 12 + 8 px макета
            log.Margin = new Padding(0, ui.S(hasAction ? 20 : 20), 0, 0);
            if (onLog != null) log.LinkClicked += delegate { onLog(log, EventArgs.Empty); };
            AddCentered(box, log);

            Controls.Add(box, 0, 1);
            SizeChanged += delegate { FitMessage(); };
        }

        private static void AddCentered(TableLayoutPanel box, Control c)
        {
            c.Anchor = AnchorStyles.None;
            box.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            box.Controls.Add(c, 0, box.RowStyles.Count - 1);
            box.RowCount = box.RowStyles.Count;
        }

        /// <summary>Фраза не шире 380 px макета и не шире самой области — в узком окне переносится.</summary>
        private void FitMessage()
        {
            int w = Math.Max(ui.S(120), Math.Min(ui.S(380), ClientSize.Width - ui.S(8)));
            if (message.MaximumSize.Width != w) message.MaximumSize = new Size(w, 0);
        }
    }
}
