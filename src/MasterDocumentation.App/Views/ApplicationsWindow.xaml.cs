using System.Diagnostics;
using System.Windows;
using MasterDocumentation.Utilities;
namespace MasterDocumentation.Views;
public partial class ApplicationsWindow:Window
{public ApplicationsWindow()=>InitializeComponent();private static void Run(string file,string? args=null)=>Process.Start(new ProcessStartInfo(file,args??""){UseShellExecute=true});private void Snipping_Click(object s,RoutedEventArgs e)=>Run("ms-screenclip:");private void Paint_Click(object s,RoutedEventArgs e)=>Run("mspaint.exe");private void Calculator_Click(object s,RoutedEventArgs e)=>Run("calc.exe");private void Assets_Click(object s,RoutedEventArgs e)=>Run("explorer.exe",$"\"{AppPaths.Assets}\"");}
