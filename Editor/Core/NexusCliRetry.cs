using System;
using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json;
using UnityEditor;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Повтор команды, которая ответила «ещё не готово» (<see cref="NexusCommandContext.RetryLater"/>): задание вида
    /// <c>retry</c> перезапускает её раз в 0,25 с, пока она не выполнится или не истечёт срок. Вызывающий (клиент,
    /// шаг задачи) ждёт один итог, как у любой долгой команды.
    /// </summary>
    [InitializeOnLoad]
    internal static class NexusCliRetry
    {
        public const string Kind = "retry";
        private const double Interval = 0.25;

        static NexusCliRetry()
        {
            NexusCliJobs.Register(Kind, Step);
        }

        /// <summary>Начать повторы команды контекста (после её первого выполнения с RetryLater).</summary>
        internal static void Start(NexusCommandContext ctx)
        {
            var job = NexusCliJobs.Start(Kind, ctx.RetryTimeout, ctx);
            job.Set("command", ctx.Name);
            job.Set("args", JsonConvert.SerializeObject(ctx.Raw ?? Array.Empty<string>()));
            job.Set("cwd", ctx.WorkingDirectory);
            if (ctx.Force) job.Set("force", "1");
            if (ctx.Task != null) job.Set("task", ctx.Task);
            NexusCliJobs.SetPhase(job, "жду: " + ctx.RetryReason);
            NexusCliJobs.Save();
        }

        private static void Step(NexusCliJob job)
        {
            var now = NexusCliJobs.Now;
            if (now - job.Anchor < Interval) return;
            job.Anchor = now;

            List<string> args;
            try
            {
                args = JsonConvert.DeserializeObject<List<string>>(job.Get("args", "[]")) ?? new List<string>();
            }
            catch
            {
                args = new List<string>();
            }

            var ctx = new NexusCommandContext(job.Get("command"), args, job.Get("cwd"))
            {
                Force = job.Get("force") == "1",
                Task = job.Get("task"),
            };

            var exit = NexusCommands.Execute(ctx);
            if (exit == NexusCommandContext.Exit.Ok && ctx.RetryReason != null)
            {
                NexusCliJobs.SetPhase(job, "жду: " + ctx.RetryReason);
                return;
            }

            var output = ctx.Output;
            if (ctx.Job != null && ctx.Job != job.Id && NexusCliJobs.IsRunning(ctx.Job))
                output += $"→ продолжается заданием {ctx.Job}: job.status {ctx.Job}\n";
            NexusCliJobs.Finish(job, exit, output, ctx.Data);
        }
    }
}
