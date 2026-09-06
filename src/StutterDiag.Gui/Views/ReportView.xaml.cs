using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StutterDiag.Gui.ViewModels;

namespace StutterDiag.Gui.Views;

public partial class ReportView : UserControl
{
    public ReportView() => InitializeComponent();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportViewModel vm) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Choose the report output folder",
            Multiselect = false
        };

        if (Directory.Exists(vm.OutputFolder))
            dialog.InitialDirectory = vm.OutputFolder;

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            vm.OutputFolder = dialog.FolderName;
    }
}
