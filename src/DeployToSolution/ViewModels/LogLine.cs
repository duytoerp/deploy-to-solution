using System;
using System.Windows;
using System.Windows.Media;

namespace DeployToSolution.ViewModels
{
    public enum LogLevel { Info, Good, Warn, Error, Step }

    public class LogLine
    {
        public DateTime At { get; } = DateTime.Now;
        public LogLevel Level { get; set; } = LogLevel.Info;
        public string Text { get; set; }

        public string Display => $"{At:HH:mm:ss}  {Text}";

        public Brush Brush => Level switch
        {
            LogLevel.Good => Brushes.SeaGreen,
            LogLevel.Warn => Brushes.DarkOrange,
            LogLevel.Error => Brushes.Crimson,
            LogLevel.Step => Brushes.MediumPurple,
            _ => Brushes.DimGray
        };

        public FontWeight Weight => Level == LogLevel.Step ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
