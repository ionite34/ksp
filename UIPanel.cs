using System;
using KRPC.Client;
using KRPC.Client.Services.UI;
using NLog;

namespace ksp
{
    internal class UIPanel: IDisposable
    {
        private readonly Connection connection;
        private readonly Canvas canvas;
        private readonly Panel panel;
        private Text text;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool Visible
        {
            get => panel.Visible;
            set => panel.Visible = value;
        }

        private Tuple<double, double> ScreenSize => canvas.RectTransform.Size;
        
        public UIPanel(Connection connection, Tuple<double, double> size = null)
        {
            if (size == null)
            {
                size = Tuple.Create(200.0, 100.0);
            }

            this.connection = connection;
            canvas = connection.UI().StockCanvas;
            panel = canvas.AddPanel();
            panel.Visible = false;
            
            var rect = panel.RectTransform;
            rect.Size = size;
            rect.Position = Tuple.Create((110 - (ScreenSize.Item1) / 2), 0.0);
        }

        public void Dispose()
        {
            Logger.Debug("Running UIPanel Dispose");
            panel.Remove();
        }
        
        public void AddButton(string name, Action callback)
        {
            var button = panel.AddButton(name);
            button.RectTransform.Position = Tuple.Create(0.0, 20.0);

            // Set up a stream with callback
            var buttonClicked = connection.AddStream(() => button.Clicked);
            buttonClicked.AddCallback(x =>
            {
                if (!x) return;
                Logger.Info($"Button {name} callback triggered");
                callback();
                button.Clicked = false;
            });
        }
        
        public void AddText(string name)
        {
            text = panel.AddText(name);
            text.RectTransform.Position = Tuple.Create(0.0, -20.0);
            text.Color = Tuple.Create(1.0, 1.0, 1.0);
            text.Size = 18;
        }
    }
}