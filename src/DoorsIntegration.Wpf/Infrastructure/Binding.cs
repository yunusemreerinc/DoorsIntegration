using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DoorsIntegration.Wpf.Infrastructure
{
    public abstract class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
        protected void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public sealed class UiCommand : ICommand
    {
        private readonly Func<Task> execute;
        private readonly Func<bool> canExecute;
        public UiCommand(Func<Task> execute, Func<bool> canExecute) { this.execute = execute; this.canExecute = canExecute; }
        public bool CanExecute(object parameter) => canExecute();
        // View-model operations own centralized exception handling. Expose Task for deterministic hosts/tests.
        // Execute is async void (ICommand contract), so anything a view model misses is logged here
        // instead of escaping to the dispatcher and terminating the process.
        public async void Execute(object parameter)
        {
            try { await ExecuteAsync(); }
            catch (Exception ex) { Trace.TraceError("UiCommand unhandled exception: " + ex); }
        }
        public Task ExecuteAsync() => CanExecute(null) ? execute() : Task.CompletedTask;
        public event EventHandler CanExecuteChanged;
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
