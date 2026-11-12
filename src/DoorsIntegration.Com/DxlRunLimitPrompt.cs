using System;
using System.Collections.Generic;
using System.Linq;

namespace DoorsIntegration.Com
{
    internal enum PromptControlKind { Other, Button, RadioButton }

    internal sealed class PromptControl
    {
        public string Identity { get; set; }
        public string Name { get; set; }
        public PromptControlKind Kind { get; set; }
        public bool Enabled { get; set; }
        public bool Visible { get; set; }
        public bool CanAct { get; set; }
    }

    internal sealed class RunLimitPrompt
    {
        public string Identity { get; set; }
        public string Text { get; set; }
        public bool Modal { get; set; }
        public IReadOnlyList<PromptControl> Controls { get; set; } = new PromptControl[0];
    }

    internal static class RunLimitPromptMatcher
    {
        // These are supported labels, not assumed window titles, native IDs or COM API names.
        // An unrecognised/localised client is deliberately left for manual handling.
        private static readonly string[] DoubleLabels = { "double execution time", "continue doubling the timeout", "continue, doubling the timeout" };
        private static readonly string[] ContinueLabels = { "continue", "continue execution", "continue with the same timeout", "continue, with the same timeout" };
        private static readonly string[] StopLabels = { "stop", "stop execution", "halt", "halt execution" };

        internal static string Normalize(string value) => (value ?? string.Empty).Replace("&", string.Empty).Trim().TrimEnd('.', ':').ToLowerInvariant();

        public static PromptControl Match(RunLimitPrompt prompt)
        {
            if (prompt == null || !prompt.Modal || string.IsNullOrEmpty(prompt.Identity) ||
                (prompt.Text ?? string.Empty).IndexOf("DXL", StringComparison.OrdinalIgnoreCase) < 0) return null;
            var actions = prompt.Controls.Where(c => c.Kind != PromptControlKind.Other).ToArray();
            var doubles = actions.Where(c => DoubleLabels.Contains(Normalize(c.Name))).ToArray();
            var continues = actions.Where(c => ContinueLabels.Contains(Normalize(c.Name))).ToArray();
            var stops = actions.Where(c => StopLabels.Contains(Normalize(c.Name))).ToArray();
            if (doubles.Length != 1 || continues.Length != 1 || stops.Length != 1) return null;
            var chosen = doubles[0];
            if (string.IsNullOrEmpty(chosen.Identity) || !chosen.CanAct || !chosen.Enabled || !chosen.Visible) return null;
            // The reported dialog has three direct choices and no OK. Do not infer a confirmation step.
            if (actions.Length != 3 || continues[0].Kind != chosen.Kind || stops[0].Kind != chosen.Kind ||
                !continues[0].Enabled || !stops[0].Enabled || !continues[0].Visible || !stops[0].Visible) return null;
            return chosen;
        }
    }

    internal interface IRunLimitPromptBackend
    {
        IReadOnlyList<RunLimitPrompt> Scan(DoorsIntegration.Core.DoorsClientProcess client);
        // Must freshly validate process, dialog and control before acting; false means no retry.
        bool TryDouble(DoorsIntegration.Core.DoorsClientProcess client, RunLimitPrompt observed, Func<bool> mayAct);
    }

    internal sealed class RunLimitPromptPolicy
    {
        private bool waitingForDismissal;
        private string lastPromptIdentity;
        private int dismissalObservations;
        public int Doublings { get; private set; }
        public bool Stopped { get; private set; }
        public bool Tick(IReadOnlyList<RunLimitPrompt> prompts, Func<RunLimitPrompt, bool> act)
        {
            if (Stopped) return false;
            var matches = prompts.Where(p => RunLimitPromptMatcher.Match(p) != null).ToArray();
            if (matches.Length > 1) { Stopped = true; return false; }
            if (waitingForDismissal)
            {
                if (!prompts.Any(p => p.Identity == lastPromptIdentity)) waitingForDismissal = false;
                else if (++dismissalObservations >= 20) Stopped = true;
                return false;
            }
            if (matches.Length == 0) return false;
            if (Doublings >= DoorsIntegration.Core.DxlRunLimitAutomationSettings.MaximumDoublingsPerCall)
            { Stopped = true; return false; }
            // An uncertain or failed UI action must never be retried automatically.
            if (!act(matches[0])) { Stopped = true; return false; }
            Doublings++;
            lastPromptIdentity = matches[0].Identity;
            dismissalObservations = 0;
            waitingForDismissal = true;
            return true;
        }
    }
}
