using System;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"버전 {version}";
        CopyrightText.Text = $"\u00a9 {DateTime.Now.Year} Dual Inc";
    }

    private void Url_Click(object sender, MouseButtonEventArgs e)
    {
        ExternalLink.Open("https://dualsoft.com");
    }
}
