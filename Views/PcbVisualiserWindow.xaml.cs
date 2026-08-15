using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PickandPlace2026.Classes;
using System;
using WinRT.Interop;

namespace PickandPlace2026.Views
{
    public sealed partial class PcbVisualiserWindow : Window
    {
        private Board? _board;

        public PcbVisualiserWindow()
        {
            InitializeComponent();

            Title = "PCB Visualiser";

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(700, 600));

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsResizable = true;
            }
        }

        public void LoadBoard(Board board)
        {
            _board = board;
            MakePCB();
        }

        private void MakePCB()
        {
            myCanvas.Children.Clear();

            if (_board == null) return;

            foreach (BoardComponent component in _board.Components)
            {
                Rectangle rect = new Rectangle();
                double placex = component.PlacementX - 20;
                double placey = component.PlacementY - 80;

                if (component.PlacementRotate == 0)
                {
                    rect.Width = 4;
                    rect.Height = 8;
                }
                else
                {
                    rect.Width = 8;
                    rect.Height = 4;
                }

                if (component.PlacementNozzle == 2)
                {
                    if (component.PlacementRotate == 0)
                    {
                        rect.Width = 16;
                        rect.Height = 8;
                        placex -= 0.5;
                        placey -= 1;
                    }
                    else
                    {
                        rect.Width = 8;
                        rect.Height = 16;
                        placex -= 1;
                        placey -= 0.5;
                    }
                }

                rect.Fill = new SolidColorBrush(Colors.Black);

                myCanvas.Children.Add(rect);
                Canvas.SetTop(rect, placex * 7);
                Canvas.SetLeft(rect, placey * 7);
            }
        }
    }
}
