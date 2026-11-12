using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    internal sealed class WindowsRunLimitPromptBackend : IRunLimitPromptBackend
    {
        private readonly DoorsClientProcessLocator processes = new DoorsClientProcessLocator();

        public IReadOnlyList<RunLimitPrompt> Scan(DoorsClientProcess client)
        {
            RequireIdentity(client);
            return ReadWindows(client).Select(ReadPrompt).ToArray();
        }

        public bool TryDouble(DoorsClientProcess client, RunLimitPrompt observed, Func<bool> mayAct)
        {
            RequireIdentity(client);
            if (!mayAct()) return false;
            var windows = ReadWindows(client);
            var matches = windows.Select(w => new { Element = w, Prompt = ReadPrompt(w) })
                .Where(w => RunLimitPromptMatcher.Match(w.Prompt) != null).ToArray();
            if (matches.Length != 1 || matches[0].Prompt.Identity != observed.Identity) return false;
            var expected = RunLimitPromptMatcher.Match(observed);
            var selected = RunLimitPromptMatcher.Match(matches[0].Prompt);
            if (expected == null || selected.Identity != expected.Identity || selected.Name != expected.Name) return false;
            var controls = Descendants(matches[0].Element);
            var element = controls.SingleOrDefault(c => Identity(c) == selected.Identity);
            if (element == null || !element.Current.IsEnabled || element.Current.IsOffscreen) return false;
            object pattern;
            if (selected.Kind == PromptControlKind.Button && element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                RequireIdentity(client);
                if (!mayAct()) return false;
                ((InvokePattern)pattern).Invoke();
                return true;
            }
            if (selected.Kind == PromptControlKind.RadioButton && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
            {
                var selection = (SelectionItemPattern)pattern;
                if (selection.Current.IsSelected) return false;
                RequireIdentity(client);
                if (!mayAct()) return false;
                selection.Select();
                return true;
            }
            return false;
        }

        private void RequireIdentity(DoorsClientProcess client)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
                throw new InvalidOperationException("UI Automation requires its dedicated MTA worker.");
            if (!processes.Matches(client)) throw new InvalidOperationException("The bound DOORS client changed or multiple clients are running. Rebind automation.");
        }

        private static AutomationElement[] ReadWindows(DoorsClientProcess client)
        {
            // Never enumerate all descendants of the desktop. Only the selected process's top-level windows.
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, client.ProcessId));
            if (windows.Count > 32) throw new InvalidOperationException("Too many client windows; automation stopped.");
            return windows.Cast<AutomationElement>().Where(w => w.Current.ControlType == ControlType.Window).ToArray();
        }

        private static RunLimitPrompt ReadPrompt(AutomationElement window)
        {
            object pattern;
            bool modal = window.TryGetCurrentPattern(WindowPattern.Pattern, out pattern) && ((WindowPattern)pattern).Current.IsModal;
            if (!modal) return new RunLimitPrompt { Identity = Identity(window) };
            var children = Descendants(window);
            return new RunLimitPrompt
            {
                Identity = Identity(window), Modal = true,
                Text = window.Current.Name + " " + string.Join(" ", children.Where(c => c.Current.ControlType == ControlType.Text).Select(c => c.Current.Name)),
                Controls = children.Select(ReadControl).Where(c => c.Kind != PromptControlKind.Other).ToArray()
            };
        }

        private static PromptControl ReadControl(AutomationElement element)
        {
            var current = element.Current;
            var kind = current.ControlType == ControlType.Button ? PromptControlKind.Button :
                current.ControlType == ControlType.RadioButton ? PromptControlKind.RadioButton : PromptControlKind.Other;
            object pattern;
            return new PromptControl
            {
                Identity = Identity(element), Name = current.Name, Kind = kind,
                Enabled = current.IsEnabled, Visible = !current.IsOffscreen,
                CanAct = kind == PromptControlKind.Button ? element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern) :
                    kind == PromptControlKind.RadioButton && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)
            };
        }

        private static List<AutomationElement> Descendants(AutomationElement root)
        {
            var result = new List<AutomationElement>();
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);
            var walker = TreeWalker.ControlViewWalker;
            while (queue.Count != 0)
            {
                var parent = queue.Dequeue();
                for (var child = walker.GetFirstChild(parent); child != null; child = walker.GetNextSibling(child))
                {
                    if (child.Current.ControlType == ControlType.TitleBar) continue;
                    if (result.Count >= 128) throw new InvalidOperationException("Dialog exceeds the automation inspection limit.");
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
            return result;
        }

        private static string Identity(AutomationElement element) => string.Join(".", element.GetRuntimeId());
    }
}
