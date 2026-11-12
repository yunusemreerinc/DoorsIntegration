using System.Windows.Input;
namespace DoorsIntegration.Wpf.Infrastructure
{
    public static class WorkspaceNavigationCommands
    {
        public static RoutedUICommand GoTo { get; }=new RoutedUICommand("Go To / Git",nameof(GoTo),typeof(WorkspaceNavigationCommands));
    }
}
